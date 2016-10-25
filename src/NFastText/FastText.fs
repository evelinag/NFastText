﻿namespace NFastText

module FastTextM =
    open Matrix
    open Dictionary
    open Args
    open Model
    open System
    open System.Collections.Generic
    open System.Diagnostics
    open System.Threading
    open System.IO

    type TestResult = {
        precision : float32
        recall : float32
        nexamples : int
        k : int
    }

    type FastTextState = {
        mutable args_ : Args
        mutable dict_ : Dictionary
        mutable input_ : Matrix.Matrix
        mutable output_ : Matrix.Matrix
    }
    let createState args dict = {
        args_ = args
        dict_ = dict
        input_ = Matrix.createNull()
        output_ = Matrix.createNull()
    }

    let saveState filename state =
          use ofs = try  new System.IO.BinaryWriter(System.IO.File.Open(filename, System.IO.FileMode.Create))                    
                    with ex -> failwith "Model file cannot be opened for saving!"
          Args.save(state.args_, ofs)
          state.dict_.save(ofs)
          Matrix.save(state.input_, ofs)
          Matrix.save(state.output_, ofs)
          ofs.Close()

    let loadState(filename : string, label, verbose) =
          use ifs = try let s = System.IO.File.Open(filename, FileMode.Open, FileAccess.Read, FileShare.Read)
                        new System.IO.BinaryReader(s)
                    with ex -> failwith "Model file cannot be opened for loading!"
          try
              let args = Args.load(ifs)
              let dict_ = Dictionary(args, label, verbose)
              dict_.load(ifs)
              let input_ = Matrix.load(ifs)
              let output_ = Matrix.load(ifs)
              {
                  args_ = args
                  dict_ = dict_
                  input_ = input_
                  output_ = output_
              }
          finally 
            ifs.Close()

    let getVector state (word : String) =
          let ngrams = state.dict_.getNgrams(word)
          let vec = createVector(state.args_.dim)
          for i = 0 to ngrams.Count - 1 do
             vec.AddRow(state.input_, ngrams.[i])
          if ngrams.Count > 0 
          then vec.Mul(1.0f / float32(ngrams.Count))
          vec

    let saveVectors(state, output : string) =
          use ofs = try new System.IO.StreamWriter(output + ".vec") 
                    with ex -> failwith "Error opening file for saving vectors."
          ofs.WriteLine(sprintf "%d %d" (state.dict_.nwords()) (state.args_.dim))
          for i = 0 to state.dict_.nwords() - 1 do
            let word = state.dict_.getWord(i)
            let vec = getVector state word
            ofs.WriteLine(sprintf "%s %A" word vec)
          ofs.Close()
    
    let textVector state rng ln =
            let line,_ = state.dict_.mapLine rng ln
            let vec = createVector(state.args_.dim)
            state.dict_.addNgrams(line, state.args_.wordNgrams)
            vec.Zero()
            for i = 0 to line.Count - 1 do
                vec.AddRow(state.input_, line.[i])
            if line.Count > 0
            then vec.Mul(1.0f / float32(line.Count))
            vec


    let createModel state seed = 
        let model = Model.createModel(state.input_, state.output_, state.args_, seed)
        let counts = if state.args_.model = model_name.sup
                     then state.dict_.getCounts(entry_type.label).ToArray()
                     else state.dict_.getCounts(entry_type.word).ToArray()
        let engine = Model.createEngine state.args_.loss counts model.rng state.args_.neg model.osz
        model, engine
    
    let printInfo(seconds, tokenCount, lr, progress : float32, loss : float32, thread : int) =
          let t = float32(seconds) 
          let wst = float32(tokenCount) / t
          let lr = lr * (1.0f - progress)
          let eta = int(t / progress * (1.f - progress) / float32(thread))
          let etah = eta / 3600
          let etam = (eta - etah * 3600) / 60
          printf "\rProgress: %.1f%%  words/sec/thread: %.0f  lr: %.6f  loss: %.6f  eta: %dh %dm" (100.f * progress) wst lr loss etah etam

    let supervised(model : Model, engine :Model.ModelEngine,
                                 lr : float32,
                                 line : ResizeArray<int>,
                                 labels : ResizeArray<int>) =
          if labels.Count = 0 || line.Count = 0 then ()
          else let i = model.rng.DiscrUniformSample(0, labels.Count - 1)
               Model.update(line.ToArray(), labels.[i], lr) engine model

    let cbow(state, model : Model, engine :Model.ModelEngine, lr : float32, line : ResizeArray<int>) =
        let bow =  ResizeArray<int>()
        for w = 0 to line.Count - 1 do
            let boundary = model.rng.DiscrUniformSample(1, state.args_.ws)
            bow.Clear()
            for c = -boundary to boundary do
                if c <> 0 && w + c >= 0 && w + c < line.Count
                then let ngrams = state.dict_.getNgrams(line.[w + c])
                     bow.AddRange(ngrams)
            Model.update(bow.ToArray(), line.[w], lr) engine model

    let skipgram(state, model : Model, engine :Model.ModelEngine, lr : float32, line : ResizeArray<int>) =
        for w = 0 to line.Count - 1 do
            let boundary = model.rng.DiscrUniformSample(1, state.args_.ws)
            let ngrams = state.dict_.getNgrams(line.[w])
            for c = -boundary to boundary do
                if c <> 0 && w + c >= 0 && w + c < line.Count
                then Model.update(ngrams.ToArray(), line.[w + c], lr) engine model
        
    let test(state, model : Model, engine :Model.ModelEngine, lines, k : int) =
        let mutable nexamples = 0
        let mutable nlabels = 0
        let mutable precision = 0.0f
        let lines = lines |> Seq.map (state.dict_.mapLine model.rng) 
        for line,labels in lines do
            state.dict_.addNgrams(line, state.args_.wordNgrams);
            if (labels.Count > 0 && line.Count > 0) 
            then
                let predictions = ResizeArray<KeyValuePair<float32,int>>()
                Model.predict(line.ToArray(), k, predictions) engine model
                for i = 0 to predictions.Count - 1 do
                    if labels.Contains(predictions.[i].Value) 
                    then precision <- precision + 1.0f
                nexamples <- nexamples + 1
                nlabels <- nlabels + labels.Count
        {
        precision = precision / float32(k * nexamples)
        recall = precision / float32(nlabels)
        nexamples = nexamples
        k  = k
        }

          
    let predict state (model : Model) (engine :Model.ModelEngine) (k : int) line =
            let line,_ = state.dict_.mapLine model.rng line
            state.dict_.addNgrams(line, state.args_.wordNgrams)
            if line.Count = 0 
            then None
            else
                let predictions = ResizeArray<KeyValuePair<float32,int>>()
                Model.predict(line.ToArray(), k, predictions) engine model
                let mutable res = []
                for i = 0 to predictions.Count - 1 do
                    if i > 0 then printf " "
                    let l = state.dict_.getLabel(predictions.[i].Value)
                    let prob = exp(predictions.[i].Key)
                    res <- (l,prob) :: res
                Some res
    type ResponseLine = float32 * string[][]
    type RequestLine = int  * float32 * AsyncReplyChannel<ResponseLine> 

    let linesSource (dict:Dictionary) args (src : seq<_>) threads verbose (tkn:CancellationToken)  =
       let cts = new CancellationTokenSource()
       let src = src |> Seq.chunkBySize 10
       MailboxProcessor<RequestLine>.Start(fun inbox ->
                    let lineSrc = src.GetEnumerator()
                    let start = Stopwatch.StartNew()
                    let ntokens = dict.ntokens()
                    let mutable tokenCount = 0L
                    async { 
                        while not tkn.IsCancellationRequested do
                            let! (count, loss, replyChannel) = inbox.Receive()
                            tokenCount <- tokenCount + int64(count)
                            let progress = float32(tokenCount) / float32(args.epoch * ntokens)
                            let lr = args.lr * (1.0f - progress)
                            lineSrc.MoveNext() |> ignore
                            replyChannel.Reply(lr, lineSrc.Current)
                            if not cts.IsCancellationRequested
                            then
                                if verbose > 1 && tokenCount % int64(args.lrUpdateRate * threads) < int64(count) 
                                then printInfo(start.Elapsed.TotalSeconds, tokenCount, args.lr, progress, loss, threads)
                            
                                if tokenCount >= int64(args.epoch * ntokens)
                                then  printInfo(start.Elapsed.TotalSeconds, tokenCount, args.lr, 1.0f, loss, threads)
                                      printfn ""
                                      cts.Cancel()
                    }) , cts.Token

    let worker state (source:MailboxProcessor<RequestLine>) (tkn:CancellationToken) threadId =
        let model, engine = createModel state threadId
        let mutable count = 0
        async{
            while not tkn.IsCancellationRequested do
                let! lr, lines = source.PostAndAsyncReply(fun replyChannel -> count, Model.getLoss model, replyChannel)
                count <- 0
                for line in lines do
                    let line, labels = state.dict_.mapLine (model.rng) line
                    match state.args_.model with
                        | model_name.sup -> state.dict_.addNgrams(line, state.args_.wordNgrams)
                                            supervised(model, engine, lr, line, labels)
                        | model_name.cbow -> cbow(state, model, engine, lr, line)
                        | model_name.sg -> skipgram(state, model, engine, lr, line)
                        | _ -> failwith "not supported model"
                    count <- count + line.Count + labels.Count
        }


    let train state verbose src threads =
          state.input_ <- Matrix.create(state.dict_.nwords() + int(state.args_.bucket), state.args_.dim)
          if state.args_.model = model_name.sup
          then state.output_ <- Matrix.create(state.dict_.nlabels(), state.args_.dim)
          else state.output_ <- Matrix.create(state.dict_.nwords(), state.args_.dim)
          state.input_.Uniform(1.0f / float32(state.args_.dim))
          state.output_.Zero()
          
          let cts = new CancellationTokenSource()
          let src, tkn = linesSource state.dict_ state.args_ src threads verbose cts.Token
          let workers = [0..threads - 1] |> Seq.map (worker state src tkn)
          Async.Parallel workers |> Async.Ignore |> Async.RunSynchronously
          cts.Cancel()
          state
          


