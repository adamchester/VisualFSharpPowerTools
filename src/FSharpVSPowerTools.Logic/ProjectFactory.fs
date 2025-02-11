﻿namespace FSharpVSPowerTools.ProjectSystem

open System
open Microsoft.FSharp.Compiler.SourceCodeServices
open Microsoft.VisualStudio.Shell.Interop
open System.ComponentModel.Composition
open Microsoft.VisualStudio.Shell
open EnvDTE
open FSharpVSPowerTools
open System.IO
open System.Diagnostics
open System.Collections.Generic
open Microsoft.VisualStudio.Text

[<NoComparison; NoEquality>]
type private CacheMessage<'K, 'V> =
    | Get of 'K * (unit -> 'V) * AsyncReplyChannel<'V>
    | TryGet of 'K * AsyncReplyChannel<'V option>
    | Remove of 'K
    | Clear

type private Cache<'K, 'V when 'K: comparison>() =
    let disposeValue value =
        match box value with
        | :? IDisposable as d -> d.Dispose()
        | _ -> ()

    let agent = MailboxProcessor.Start(fun inbox ->
        let rec loop (cache: Map<'K, 'V>) = 
            async {
                let! msg = inbox.Receive()
                return! loop (
                    match msg with
                    | Get (key, creator, r) ->
                        match cache |> Map.tryFind key with
                        | Some value ->
                            //debug "[Project cache] Return from cache for %A" key
                            r.Reply value
                            cache
                        | None ->
                            let value = creator()
                            //debug "[Project cache] Creating new value for %A" key
                            r.Reply value
                            cache |> Map.add key value
                    | TryGet (key, r) ->
                        r.Reply (cache |> Map.tryFind key)
                        cache
                    | Remove key -> 
                        match cache |> Map.tryFind key with
                        | Some value -> 
                            disposeValue value
                            cache |> Map.remove key
                        | _ -> cache
                    | Clear -> 
                        cache |> Map.toSeq |> Seq.iter (fun (_, value) -> disposeValue value)
                        Map.empty) 
            }
        loop Map.empty)
    do agent.Error.Add (fail "%O")
    member __.Get key creator = agent.PostAndReply (fun r -> Get (key, creator, r))
    member __.TryGet key = agent.PostAndReply (fun r -> TryGet (key, r))
    member __.Remove key = agent.Post (Remove key)
    member __.Clear() = agent.Post Clear

type private ProjectUniqueName = string

[<Export>]
type ProjectFactory
    [<ImportingConstructor>] 
    ([<Import(typeof<SVsServiceProvider>)>] serviceProvider: IServiceProvider,
     openDocumentsTracker: OpenDocumentsTracker,
     vsLanguageService: VSLanguageService) =
    let dte = serviceProvider.GetService<DTE, SDTE>()
    let events: EnvDTE80.Events2 option = tryCast dte.Events
    let cache = Cache<ProjectUniqueName, ProjectProvider>()

    let mayReferToSameBuffer (buffer: ITextBuffer) filePath =
        match openDocumentsTracker.TryFindOpenDocument(filePath) with
        | None -> true
        | Some doc ->
            buffer = doc.Document.TextBuffer

    let onProjectChanged (project: Project) = 
        debug "[ProjectFactory] %s changed." project.Name
        cache.Remove project.FullName

    let onProjectItemChanged (projectItem: ProjectItem) =
        projectItem.VSProject |> Option.iter (fun item -> 
            cache.TryGet item.Project.FullName
            |> Option.iter (vsLanguageService.InvalidateProject >> Async.RunSynchronously) 
            onProjectChanged item.Project)

    let signatureProjectData = Dictionary()
    let fsharpProjectsCache = ref None

    let clearCaches() =
        signatureProjectData.Clear()
        fsharpProjectsCache := None
        cache.Clear()
        vsLanguageService.ClearCaches()

    let solutionBuildEventListener = new SolutionBuildEventListener(serviceProvider)
    // When active configuration changes, all project providers are stale so we clear our own caches
    do solutionBuildEventListener.ActiveConfigChanged.Add(fun _ -> clearCaches())

    do match events with
        | Some events ->
            events.SolutionEvents.add_AfterClosing (fun _ -> clearCaches())
            events.ProjectItemsEvents.add_ItemRenamed (fun p _ -> onProjectItemChanged p)
            events.ProjectItemsEvents.add_ItemRemoved (fun p -> onProjectItemChanged p)
            events.ProjectItemsEvents.add_ItemAdded (fun p -> onProjectItemChanged p)
            events.ProjectsEvents.add_ItemRemoved (fun p -> onProjectChanged p)
            events.ProjectsEvents.add_ItemRenamed (fun p _ -> onProjectChanged p)

            events.SolutionEvents.add_ProjectAdded (fun _ -> 
                fsharpProjectsCache := None)
            events.SolutionEvents.add_ProjectRemoved (fun p -> 
                fsharpProjectsCache := None
                onProjectChanged p)
            events.SolutionEvents.add_ProjectRenamed (fun p _ -> 
                fsharpProjectsCache := None
                onProjectChanged p) 
            debug "[ProjectFactory] Subscribed for ProjectItemsEvents"
        | _ -> fail "[ProjectFactory] Cannot subscribe for ProjectItemsEvents"

    member __.RegisterSignatureProjectProvider(filePath: string, project: IProjectProvider) =
        // We have to keep the project provider even the buffer has been close.
        // If the buffer is reopened via Navigate Backward, we still have to colorize the text.
        // The project provider will be discard once the solution is closed.
        let signatureProject = SignatureProjectProvider(filePath, project) :> IProjectProvider
        signatureProjectData.[filePath] <- signatureProject
        signatureProject

    member __.TryGetSignatureProjectProvider(filePath: string) =
        match signatureProjectData.TryGetValue(filePath) with
        | true, project ->
            Some project
        | _ -> None

    abstract CreateForProject: Project -> IProjectProvider

    default x.CreateForProject (project: Project): IProjectProvider = 
        let createProjectProvider project = Some (x.CreateForProject project)
        cache.Get project.FullName (fun _ ->
            new ProjectProvider (project, createProjectProvider, onProjectChanged)) :> _

    member x.CreateForDocument buffer (doc: Document) =
        let filePath = doc.FullName
        Debug.Assert(mayReferToSameBuffer buffer filePath, 
                sprintf "Buffer '%A' doesn't refer to the current document '%s'." buffer filePath)
        let project = doc.ProjectItem.ContainingProject
        if not (project === null) && not (filePath === null) && isFSharpProject project then
            let projectProvider = x.CreateForProject project
            // If current file doesn't have 'BuildAction = Compile', it doesn't appear in the list of source files. 
            // Consequently, we should interpret it as a script.
            if Array.exists ((=) filePath) projectProvider.SourceFiles then
                Some projectProvider
            else
                let ext = Path.GetExtension filePath
                if isSourceExtension ext then
                    let vsVersion = VisualStudioVersion.fromDTEVersion doc.DTE.Version
                    Some (VirtualProjectProvider(buffer, filePath, vsVersion) :> _)
                else
                    None
        elif not (filePath === null) then
            let ext = Path.GetExtension filePath
            if isSourceExtension ext then
                let vsVersion = VisualStudioVersion.fromDTEVersion doc.DTE.Version
                Some (VirtualProjectProvider(buffer, filePath, vsVersion) :> _)
            elif isSignatureExtension ext then
                match signatureProjectData.TryGetValue(filePath) with
                | true, project ->
                    Some project
                | _ -> None
            else
                None
        else 
            None

    member __.ListFSharpProjectsInSolution dte =
        match !fsharpProjectsCache with
        | Some x -> x
        | None ->
            let res = listFSharpProjectsInSolution dte
            fsharpProjectsCache := Some res
            res

    member x.GetSymbolDeclarationLocation (symbol: FSharpSymbol) (currentFile: FilePath) (currentProject: IProjectProvider) =
        Debug.Assert(currentProject.SourceFiles |> Array.exists ((=) currentFile), 
            sprintf "Current file '%s' should be included in current project '%A'." currentFile currentProject.SourceFiles)
        let isPrivateToFile = 
            match symbol with
            | :? FSharpMemberOrFunctionOrValue as m -> not m.IsModuleValueOrMember
            | :? FSharpEntity as m -> m.Accessibility.IsPrivate
            | :? FSharpGenericParameter -> true
            | :? FSharpUnionCase as m -> m.Accessibility.IsPrivate
            | :? FSharpField as m -> m.Accessibility.IsPrivate
            | _ -> false

        if isPrivateToFile then 
            Some SymbolDeclarationLocation.File 
        else
            let isSymbolLocalForProject = TypedAstUtils.isSymbolLocalForProject symbol 
            match Option.orElse symbol.ImplementationLocation symbol.DeclarationLocation with
            | Some loc ->
                Logging.logInfo (fun _ -> sprintf "Trying to find symbol '%O' declared at '%O' from current file '%O'..." symbol loc.FileName currentFile)
                let filePath = Path.GetFullPathSafe loc.FileName
                if currentProject.IsForStandaloneScript && filePath = currentFile then 
                    Some SymbolDeclarationLocation.File
                elif currentProject.IsForStandaloneScript then
                    // The standalone script might include other files via '#load'
                    // These files appear in project options and the standalone file 
                    // should be treated as an individual project
                    Some (SymbolDeclarationLocation.Projects ([currentProject], isSymbolLocalForProject))
                else
                    let allProjects = x.ListFSharpProjectsInSolution dte |> List.map x.CreateForProject
                    let allProjectFileNames =
                        lazy (allProjects |> List.map (fun p -> Path.GetFullPathSafe(p.ProjectFileName)))
                    Debug.Assert(
                        allProjectFileNames.Value |> List.exists (fun projectFileName -> 
                                                        Path.GetFullPathSafe(currentProject.ProjectFileName) = projectFileName), 
                        sprintf "Current project '%s' should appear in the project list '%A'." currentProject.ProjectFileName allProjectFileNames.Value)
                    match allProjects |> List.filter (fun p -> p.SourceFiles |> Array.exists ((=) filePath)) with
                    | [] -> None
                    | projects -> Some (SymbolDeclarationLocation.Projects (projects, isSymbolLocalForProject))
            | None -> None

    member x.GetDependentProjects (dte: DTE) (projects: IProjectProvider list) =
        let projectFileNames = projects |> List.map (fun p -> p.ProjectFileName.ToLowerInvariant()) |> set
        x.ListFSharpProjectsInSolution dte
        |> Seq.map x.CreateForProject
        |> Seq.filter (fun p -> 
            p.GetReferencedProjects() 
            |> List.exists (fun p -> 
                projectFileNames |> Set.contains (p.ProjectFileName.ToLowerInvariant())))
        |> Seq.append projects
        |> Seq.distinctBy (fun p -> p.ProjectFileName.ToLowerInvariant())
        |> Seq.toList

    interface IDisposable with
        member __.Dispose() = 
            (solutionBuildEventListener :> IDisposable).Dispose()
