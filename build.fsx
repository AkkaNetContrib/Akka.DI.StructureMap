#I @"tools/FAKE/tools"
#r "FakeLib.dll"

open System
open System.IO
open System.Text

open Fake
open Fake.DotNetCli
open Fake.DocFxHelper

// Variables
let configuration = "Release"

// Directories
let output = __SOURCE_DIRECTORY__  @@ "bin"
let outputTests = output @@ "tests"
let outputBinaries = output @@ "binaries"
let outputNuGet = output @@ "nuget"

let buildNumber = environVarOrDefault "BUILD_NUMBER" "0"
let preReleaseVersionSuffix = "beta" + (if (not (buildNumber = "0")) then (buildNumber) else "")
let versionSuffix = 
    match (getBuildParam "nugetprerelease") with
    | "dev" -> preReleaseVersionSuffix
    | _ -> ""

Target "Clean" (fun _ ->
    CleanDir output
    CleanDir outputTests
    CleanDir outputBinaries
    CleanDir outputNuGet

    CleanDirs !! "./src/**/bin"
    CleanDirs !! "./src/**/obj"
)

Target "RestorePackages" (fun _ ->
    DotNetCli.Restore
        (fun p -> 
            { p with
                Project = "./src/Akka.DI.StructureMap.sln"
                NoCache = false })
)

Target "Build" (fun _ ->
        DotNetCli.Build
            (fun p -> 
                { p with
                    Project = "./src/Akka.DI.StructureMap.sln"
                    Configuration = configuration })
)

//--------------------------------------------------------------------------------
// Tests targets 
//--------------------------------------------------------------------------------

Target "RunTests" (fun _ ->
    let projects = !! "./**/*.Tests.csproj"

    let runSingleProject project =
        DotNetCli.RunCommand
            (fun p -> 
                { p with 
                    WorkingDir = (Directory.GetParent project).FullName
                    TimeOut = TimeSpan.FromMinutes 10. })
                (sprintf "xunit -parallel none -teamcity -xml %s_xunit.xml" (outputTests @@ fileNameWithoutExt project)) 

    projects |> Seq.iter (runSingleProject)
)

//--------------------------------------------------------------------------------
// Nuget targets 
//--------------------------------------------------------------------------------

Target "CreateNuget" (fun _ ->
    DotNetCli.Pack
        (fun p -> 
            { p with
                Project = "./src/Akka.DI.StructureMap/Akka.DI.StructureMap.csproj"
                Configuration = configuration
                AdditionalArgs = ["--include-symbols"]
                VersionSuffix = versionSuffix
                OutputPath = outputNuGet })
)

Target "PublishNuget" (fun _ ->
    let nugetExe = FullName @"./tools/nuget.exe"
    let rec publishPackage url accessKey trialsLeft packageFile =
        let tracing = enableProcessTracing
        enableProcessTracing <- false
        let args p =
            match p with
            | (pack, key, "") -> sprintf "push \"%s\" %s" pack key
            | (pack, key, url) -> sprintf "push \"%s\" %s -source %s" pack key url

        tracefn "Pushing %s Attempts left: %d" (FullName packageFile) trialsLeft
        try 
            let result = ExecProcess (fun info -> 
                    info.FileName <- nugetExe
                    info.WorkingDirectory <- (Path.GetDirectoryName (FullName packageFile))
                    info.Arguments <- args (packageFile, accessKey,url)) (System.TimeSpan.FromMinutes 1.0)
            enableProcessTracing <- tracing
            if result <> 0 then failwithf "Error during NuGet symbol push. %s %s" nugetExe (args (packageFile, "key omitted",url))
        with exn -> 
            if (trialsLeft > 0) then (publishPackage url accessKey (trialsLeft-1) packageFile)
            else raise exn
    let shouldPushNugetPackages = hasBuildParam "nugetkey"
    let shouldPushSymbolsPackages = (hasBuildParam "symbolspublishurl") && (hasBuildParam "symbolskey")
    
    if (shouldPushNugetPackages || shouldPushSymbolsPackages) then
        printfn "Pushing nuget packages"
        if shouldPushNugetPackages then
            let normalPackages= 
                !! (outputNuGet @@ "*.nupkg") 
                -- (outputNuGet @@ "*.symbols.nupkg") |> Seq.sortBy(fun x -> x.ToLower())
            for package in normalPackages do
                try
                    publishPackage (getBuildParamOrDefault "nugetpublishurl" "") (getBuildParam "nugetkey") 3 package
                with exn ->
                    printfn "%s" exn.Message

        if shouldPushSymbolsPackages then
            let symbolPackages= !! (outputNuGet @@ "*.symbols.nupkg") |> Seq.sortBy(fun x -> x.ToLower())
            for package in symbolPackages do
                try
                    publishPackage (getBuildParam "symbolspublishurl") (getBuildParam "symbolskey") 3 package
                with exn ->
                    printfn "%s" exn.Message
)

//--------------------------------------------------------------------------------
// Help 
//--------------------------------------------------------------------------------

Target "Help" <| fun _ ->
    List.iter printfn [
      "usage:"
      "build [target]"
      ""
      " Targets for building:"
      " * Build      Builds"
      " * Nuget      Create and optionally publish nugets packages"
      " * RunTests   Runs tests"
      " * All        Builds, run tests, creates and optionally publish nuget packages"
      ""
      " Other Targets"
      " * Help       Display this help" 
      " * HelpNuget  Display help about creating and pushing nuget packages" 
      ""]

Target "HelpNuget" <| fun _ ->
    List.iter printfn [
      "usage: "
      "build Nuget [nugetkey=<key> [nugetpublishurl=<url>]] "
      "            [symbolskey=<key> symbolspublishurl=<url>] "
      "            [nugetprerelease=<prefix>]"
      ""
      "Arguments for Nuget target:"
      "   nugetprerelease=<prefix>   Creates a pre-release package."
      "                              The version will be version-prefix<date>"
      "                              Example: nugetprerelease=dev =>"
      "                                       0.6.3-dev1408191917"
      ""
      "In order to publish a nuget package, keys must be specified."
      "If a key is not specified the nuget packages will only be created on disk"
      "After a build you can find them in bin/nuget"
      ""
      "For pushing nuget packages to nuget.org and symbols to symbolsource.org"
      "you need to specify nugetkey=<key>"
      "   build Nuget nugetKey=<key for nuget.org>"
      ""
      "For pushing the ordinary nuget packages to another place than nuget.org specify the url"
      "  nugetkey=<key>  nugetpublishurl=<url>  "
      ""
      "For pushing symbols packages specify:"
      "  symbolskey=<key>  symbolspublishurl=<url> "
      ""
      "Examples:"
      "  build Nuget                      Build nuget packages to the bin/nuget folder"
      ""
      "  build Nuget nugetprerelease=dev  Build pre-release nuget packages"
      ""
      "  build Nuget nugetkey=123         Build and publish to nuget.org and symbolsource.org"
      ""
      "  build Nuget nugetprerelease=dev nugetkey=123 nugetpublishurl=http://abc"
      "              symbolskey=456 symbolspublishurl=http://xyz"
      "                                   Build and publish pre-release nuget packages to http://abc"
      "                                   and symbols packages to http://xyz"
      ""]

//--------------------------------------------------------------------------------
//  Target dependencies
//--------------------------------------------------------------------------------

Target "BuildRelease" DoNothing
Target "Nuget" DoNothing

// build dependencies
"Clean" ==> "RestorePackages" ==> "Build" ==> "BuildRelease"

// tests dependencies
"Clean" ==> "RestorePackages" ==> "RunTests"

// nuget dependencies
"Clean" ==> "RestorePackages" ==> "Build" ==> "CreateNuget"
"CreateNuget" ==> "PublishNuget" ==> "Nuget"

// all
Target "All" DoNothing
"BuildRelease" ==> "All"
"RunTests" ==> "All"
"Nuget" ==> "All"

RunTargetOrDefault "Help"