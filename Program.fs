// $begin{copyright}
//
// This file is part of WebSharper
//
// Copyright (c) 2008-2018 IntelliFactory
//
// Licensed under the Apache License, Version 2.0 (the "License"); you
// may not use this file except in compliance with the License.  You may
// obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
// implied.  See the License for the specific language governing
// permissions and limitations under the License.
//
// $end{copyright}
#nowarn "44"
open System
open System.Diagnostics
open Argu
open Fake.Core
open System.IO.Pipes
open System.IO
open System.Runtime.Serialization.Formatters.Binary
open System.Text
open WebSharper.Compiler.WsFscServiceCommon
open System.Threading


let (|StdOut|_|) (str: string) =
    if str.StartsWith("n: ") then
        str.Substring(3) |> Some
    else
        None

let (|StdErr|_|) (str: string) =
    if str.StartsWith("e: ") then
        str.Substring(3) |> Some
    else
        None

let (|Finish|_|) (str: string) =
    if str.StartsWith("x: ") then
        let trimmed = 
            str.Substring(3)
        trimmed
        |> System.Int32.Parse
        |> Some
    else
        None

type RidEnum =
    | ``win-x64`` = 1
    | ``linux-x64`` = 2
    | ``linux-musl-x64`` = 3

type Argument =
    | [<CliPrefix(CliPrefix.None)>] Start of ParseResults<StartArguments>
    | [<CliPrefix(CliPrefix.None)>] Stop of ParseResults<StopArguments>
    | [<CliPrefix(CliPrefix.None); SubCommand>] List
    | [<CliPrefix(CliPrefix.None); SubCommand>] Build

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Start _ -> "Starts wsfscservice with the given RID (Runtime Identifier) (win-x64 or linux-x64 or linux-musl-x64). And version. If no value given for version, the latest will be used."
            | Stop _ -> "Sends a stop signal for the wsfscservice with the given version. If no version given it's all running instances. If --force given kills the process instead of stop signal."
            | List -> "Lists running wsfscservice versions."
            | Build -> "Build WebSharper project in the current folder. Using cached build information where possible."
and StartArguments =
    | [<Mandatory; AltCommandLine("-r")>] RID of RidEnum
    | [<AltCommandLine("-v")>] Version of semver:string

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | RID _ -> "RID must be one of win-x64, linux-x64, linux-musl-x64."
            | Version _ -> "wsfscservice version. If no value given, the latest will be used."

and StopArguments =
    | [<AltCommandLine("-v")>] Version of semver:string
    | [<AltCommandLine("-f")>] Force

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Version _ -> "wsfscservice version. If empty all running instances"
            | Force -> "kills the service instead of sending stop signal"

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<Argument>(programName = "dotnet-ws.exe", helpTextMessage = "dotnet-ws is a dotnet tool for WebSharper", checkStructure = true)
    try
        let result = parser.Parse argv
        let subCommand = result.GetSubCommand()

        let clientPipeForLocation (location:string) =
            let location = IO.Path.GetDirectoryName(location)
            let pipeName = (location, "WsFscServicePipe") |> Path.Combine |> hashPath
            let serverName = "." // local machine server name
            new NamedPipeClientStream( serverName, //server name, local machine is .
                                 pipeName, // name of the pipe,
                                 PipeDirection.InOut, // direction of the pipe 
                                 PipeOptions.WriteThrough // the operation will not return the control until the write is completed
                                 ||| PipeOptions.Asynchronous)

        let sendOneMessage (clientPipe: NamedPipeClientStream) (message: ArgsType) =
            let bf = new BinaryFormatter();
            use ms = new MemoryStream()
            // args going binary serialized to the service.
            bf.Serialize(ms, message);
            ms.Flush();
            ms.Position <- 0L
            clientPipe.Connect()
            let tryCompileTimeOutSeconds = 5.0
            use cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(tryCompileTimeOutSeconds))
            ms.CopyToAsync(clientPipe, cancellationTokenSource.Token) |> Async.AwaitTask |> Async.RunSynchronously
            clientPipe.Flush()

        match subCommand with
        | Start startParams ->
            let rid = startParams.GetResult RID
            let ridString = rid.ToString()
            try
                let nugetRoot = 
                    match Environment.GetEnvironmentVariable("NUGET_PACKAGES") |> Option.ofObj with
                    | Some x -> x
                    //%USERPROFILE%\.nuget\packages win
                    //~/.nuget/packages linux
                    | None -> Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages")
                let fsharpFolder = Path.Combine(nugetRoot, "websharper.fsharp")
                try
                    let version =
                        match startParams.TryGetResult StartArguments.Version with
                        | Some semver -> semver
                        | None ->
                            Directory.EnumerateDirectories(fsharpFolder)
                            |> Seq.map (fun x ->
                                let justFolder = DirectoryInfo(x).Name
                                SemVer.parse(justFolder))
                            |> Seq.max
                            |> (fun x -> x.ToString())
                    // start a detached wsfscservice.exe. Platform specific.
                    let cmdName = if System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) then
                                      "wsfscservice_start.cmd" else "wsfscservice_start.sh"
                    let cmdFullPath = Path.Combine(fsharpFolder, version, "tools", "net5.0", ridString, cmdName)
                    if File.Exists(cmdFullPath) |> not then
                        failwith "dummy" // It will become "Couldn't find wsfscservice_start"
                    let startInfo = ProcessStartInfo(cmdFullPath)
                    startInfo.CreateNoWindow <- true
                    startInfo.UseShellExecute <- false
                    startInfo.WindowStyle <- ProcessWindowStyle.Hidden
                    try
                        Process.Start(startInfo) |> ignore
                        printfn "version: %s; path: %s Started" version cmdFullPath
                        0
                    with ex ->
                        eprintfn "Couldn't start wsfscservice_start in %s (%s)" cmdFullPath ex.Message
                        1
                with
                | :? Argu.ArguParseException ->
                    reraise()
                | _ ->
                    eprintfn "Couldn't find wsfscservice_start"
                    1
            with
            | :? Argu.ArguParseException ->
                reraise()
            | _ ->
                eprintfn "Couldn't find nuget packages root folder"
                1
        | Stop stopParams -> 
            let tryKill (returnCode, successfulErrorPrint) (proc: Process) =
                try
                    let killOrSend (x: Process) = 
                        if stopParams.Contains Force then
                            x.Kill()
                            printfn "Stopped wsfscservice with PID: (%i)" x.Id
                        else
                            let clientPipe = clientPipeForLocation x.MainModule.FileName
                            sendOneMessage clientPipe {args = [|"exit"|]}
                            printfn "Stop signal sent to wsfscservice with PID: (%i)" x.Id
                    match stopParams.TryGetResult Version with
                    | Some v -> 
                        if proc.MainModule.FileVersionInfo.FileVersion = v then
                            killOrSend proc
                    | None ->
                        killOrSend proc
                    (returnCode, successfulErrorPrint)
                with
                | _ ->
                    try
                        eprintfn "Couldn't kill process with PID: (%i)" proc.Id
                        (1, successfulErrorPrint)
                    with
                    | _ -> (1, false)
            try
                let (returnCode, successfulErrorPrint) = Process.GetProcessesByName("wsfscservice") |> Array.fold tryKill (0, true)
                if successfulErrorPrint |> not then
                    eprintfn "Couldn't read processes"
                returnCode
            with
            | _ -> 1
        | List -> 
            try
                let procInfos = 
                    Process.GetProcessesByName("wsfscservice")
                    |> Array.map (fun proc -> sprintf "version: %s; path: %s" proc.MainModule.FileVersionInfo.FileVersion proc.MainModule.FileName)
                if procInfos.Length = 0 then
                    printfn "There are no wsfscservices running"
                else
                    printfn """The following wsfscservice versions are running:

  %s""" 
                        (String.Join(Environment.NewLine + "  ", procInfos))
                0
            with
            | _ ->
                eprintfn "Couldn't read processes"
                1
        | Build ->
            try
                let projectFile = 
                    Directory.EnumerateFiles(".", "*.fsproj")
                    |> Seq.tryHead
                    |> Option.bind (Path.GetFullPath >> Some)
                match projectFile with
                | Some projectFile ->
                    let trySendCompile (proc: Process) =
                        try
                            let clientPipe = clientPipeForLocation proc.MainModule.FileName
                            sendOneMessage clientPipe {args = [| sprintf "compile:%s" projectFile |]}
                            let unexpectedFinishErrorCode = -12211
                            let write = async {
                                let printResponse (message: obj) = 
                                    async {
                                        // messages on the service have n: e: or x: prefix for stdout stderr or error code kind of output
                                        match message :?> string with
                                        | StdOut n ->
                                            printfn "%s" n
                                            return None
                                        | StdErr e ->
                                            eprintfn "%s" e
                                            return None
                                        | Finish i -> 
                                            return i |> Some
                                        | x -> 
                                            let unrecognizedMessageErrorCode = -13311
                                            eprintfn "Unrecognizable message from server (%i): %s" unrecognizedMessageErrorCode x
                                            return unrecognizedMessageErrorCode |> Some
                                    }
                                let! errorCode = readingMessages clientPipe printResponse
                                match errorCode with
                                | Some x -> return x
                                | None -> 
                                    eprintfn "Listening for server finished abruptly (%i)" unexpectedFinishErrorCode
                                    return unexpectedFinishErrorCode
                                }
                            try
                                match Async.RunSynchronously(write) with
                                // wsfscservice process reported back, it doesn't have the project cached
                                | -33212 -> None
                                // the type of the project is not ws buildable (WIG or Proxy)
                                | -21233 -> None
                                | x -> x |> Some
                            with
                            | _ -> 
                                eprintfn "Listening for server finished abruptly (%i)" unexpectedFinishErrorCode
                                unexpectedFinishErrorCode |> Some
                        with
                        | _ ->
                            None
                    try
                        match Process.GetProcessesByName("wsfscservice") |> Array.tryPick trySendCompile with
                        | Some errorCode -> errorCode
                        | None ->
                            let startInfo = ProcessStartInfo("dotnet")
                            startInfo.Arguments <- "build"
                            let dotnetProc = Process.Start(startInfo)
                            dotnetProc.WaitForExit()
                            dotnetProc.ExitCode
                    with
                    | x ->
                        eprintfn "Couldn't build project: %s" x.Message
                        1
                | None ->
                    eprintfn "Couldn't find a project file in the current directory"
                    1
            with
            | :? UnauthorizedAccessException -> 
                eprintfn "Couldn't search for a project file (Unauthorized)"
                1
    with
    | :? Argu.ArguParseException as ex -> 
        eprintfn "%s" (parser.HelpTextMessage.Value + Environment.NewLine + Environment.NewLine + ex.Message)
        1