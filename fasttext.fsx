#I @".\bin\NFastText"
#r "NFastText.dll"

let timeit f =
    let stopWatch = System.Diagnostics.Stopwatch.StartNew()
    f () |> ignore
    stopWatch.Stop()
    stopWatch.Elapsed.TotalSeconds

open NFastText
open NFastText.FastTextM
let trainArgs = {
        input = "D:/ft/data/dbpedia.train"
        output = "D:/ft/result/dbpedia"
        args = { Args.supervisedArgs with
                    dim=10
                    lr = 0.1f
                    wordNgrams = 2
                    minCount = 1
                    bucket = 10000000
                    epoch = 5
               }
        thread =  4
}
let label = ByteString.fromString "__label__"
let verbose = 2
let train() =
    let output = "D:/ft/result/dbpedia"
    let state = FastTextM.createState label verbose
    let fs = FastTextM.FastText(state, label, verbose)
    let state = fs.train trainArgs
    FastTextM.saveState (output + ".bin") state 
    if state.args_.model <> Args.model_name.sup 
    then FastTextM.saveVectors(state, output)

let test() =
    let state = FastTextM.loadState("D:/ft/result/dbpedia.bin",label,verbose)
    let fs = FastTextM.FastText(state,label,verbose)
    let r = fs.test("D:/ft/data/dbpedia.test",1)
    assert(r.precision >= 0.98f) 
    assert(r.recall >= 0.98f)
    assert(r.nexamples = 70000) 

let predictRes = [|
    "__label__9"
    "__label__9"
    "__label__3"
    "__label__6"
    "__label__7"
    "__label__7"
    "__label__11"
    "__label__11"
    "__label__9"
    "__label__13"
    "__label__12"
    "__label__2"
|]

let predict() =
    let state = FastTextM.loadState("D:/ft/result/dbpedia.bin",label,verbose)
    let fs = FastTextM.FastText(state,label,verbose)
    let r = fs.predict("D:/ft/data/dbpedia.test",1)
    let r = Seq.take (predictRes.Length) r 
                |> Seq.choose id
                |> Seq.map (List.head >> fst)
                |> Array.ofSeq
    assert(r = predictRes)

[|"train", train; "test", test; "predict", predict|] 
        |> Array.map (fun (n,f) -> printfn "exec %s" n
                                   n, timeit f)
        |> Array.iter (fun (n,t) -> printfn "%s\n%f" n t)


