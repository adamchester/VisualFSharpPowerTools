﻿namespace FSharpVSPowerTools.ProjectSystem

open System
open System.IO
open System.Diagnostics
open EnvDTE
open VSLangProj
open FSharp.CompilerBinding

type ProjectProvider(currentFile : string, project : VSProject) = 
    do Debug.Assert(project <> null && project.Project <> null, "Input project should be well-formed.")

    let currentDir = Path.GetDirectoryName(currentFile)
    let getProperty (tag : string) =
        let prop = try project.Project.Properties.[tag] with _ -> null
        match prop with
        | null -> null
        | _ -> prop.Value.ToString()

    /// Wraps the given string between double quotes
    let wrap (s : string) = if s.StartsWith "\"" then s else String.Join("", "\"", s, "\"")  

    member __.ProjectFileName = 
        let fileName = getProperty "FileName"
        let projectPath = getProperty "FullPath"
        Debug.Assert(fileName <> null && projectPath <> null, "Should have a file name for the project.")
        Path.Combine(projectPath, fileName)

    member __.TargetFSharpCoreVersion = 
        getProperty "TargetFSharpCoreVersion"

    member __.TargetFramework = 
        match getProperty "TargetFrameworkVersion" with
        | null | "v4.5" | "v4.5.1" -> FSharpTargetFramework.NET_4_5
        | "v4.0" -> FSharpTargetFramework.NET_4_0
        | "v3.5" -> FSharpTargetFramework.NET_3_5
        | "v3.0" -> FSharpTargetFramework.NET_3_5
        | "v2.0" -> FSharpTargetFramework.NET_2_0
        | _ -> invalidArg "prop" "Unsupported .NET framework version"

    member private __.References = 
        project.References
        |> Seq.cast<Reference>
        // REVIEW: we may not need resolving references if VS returns enough information
        |> Seq.map (fun r -> r.Name)
        |> Seq.map (fun name -> 
            let assemblyName = if name.EndsWith ".dll" then name else name + ".dll"
            sprintf "-r:%s" (wrap assemblyName))

    member this.CompilerOptions = 
        // REVIEW: getting properties are failing since we haven't got hold of F# Project System
        let definesProp = getProperty "DefineConstants"
        let defines = 
            match definesProp with
            | null -> [||] 
            | _ -> definesProp.Split([| ';'; ','; ' ' |], StringSplitOptions.RemoveEmptyEntries)
        let debugSymbolsProp = getProperty "DebugSymbols"
        let hasDebugSymbols = debugSymbolsProp <> null && debugSymbolsProp.ToLower() = "true"
        let optimizeProp = getProperty "Optimize"
        let optimize = optimizeProp <> null && optimizeProp.ToLower() = "true"
        let generateTailcallsProp = getProperty "Tailcalls"
        let gerateTailcalls = generateTailcallsProp <> null && generateTailcallsProp.ToLower() = "true"
        let otherFlagsProp = getProperty "OtherFlags"
        let otherFlags = 
            match otherFlagsProp with
            | null -> [||]
            | _ -> otherFlagsProp.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries)
        [  
           yield "--noframework"
           for symbol in defines -> "--define:" + symbol
           if hasDebugSymbols then yield "--debug+" else yield "--debug-"
           if optimize then yield "--optimize+" else yield "--optimize-"
           if gerateTailcalls then yield "--tailcalls+" else yield "--tailcalls-"
           // TODO: This currently ignores escaping using 'wrap' function
           yield! otherFlags
           yield! this.References
        ]

    member __.SourceFiles = 
        let castSeq (xs : ProjectItems) =
            if xs = null then Seq.empty else Seq.cast<ProjectItem> xs

        let allFiles (item : ProjectItem) =
            item.ProjectItems
            |> castSeq
            // REVIEW: this may not return expected results
            |> Seq.filter (fun item -> item <> null && item.SubProject <> null)
            // REVIEW: we may not need current directory if VS can resolve absolute path
            |> Seq.map (fun item -> Path.Combine(currentDir, item.Name))

        project.Project.ProjectItems
        |> castSeq
        |> Seq.collect allFiles
        |> fun xs -> if Seq.isEmpty xs then seq [currentFile] else xs      



