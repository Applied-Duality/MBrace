(*** hide ***)
// This block of code is omitted in the generated HTML documentation. Use 
// it to define helpers that you do not want to show in the documentation.
#I "../../bin/"
#I "../../src/MBrace.Client/"

#load "preamble.fsx"
open Nessos.MBrace
open Nessos.MBrace.Lib
open Nessos.MBrace.Client

(**

# MBrace framework

The MBrace framework is an open-source distributed runtime that enables
scalable, fault-tolerant computation and data processing for the .NET/mono frameworks.
The MBrace programming model uses a distributed continuation-based approach elegantly
manifested through computation expressions in F#.

<div class="row">
  <div class="span1"></div>
  <div class="span6">
    <div class="well well-small" id="nuget">
      The MBrace framework can be <a href="https://nuget.org/packages/MBrace.Runtime">installed from NuGet</a>:
      <pre>PM> Install-Package MBrace.Runtime</pre>
    </div>
  </div>
  <div class="span1"></div>
</div>

## Example

This simple example demonstrates an MBrace computation in which files are read
from a distributed storage container and the total line count is returned.

*)

[<Cloud>]
let lineCount () = cloud {
    // get all files from container in runtime storage provider.
    let! files = CloudFile.GetFilesInContainer "path/to/container"

    // read the contents of a file and return its line count
    let count f = cloud {
        let! text = CloudFile.ReadAllText f
        return text.Split('\n').Length
    }
    
    // perform line count in parallel
    let! sizes = files |> Array.map count |> Cloud.Parallel
    return Array.sum sizes
}

let runtime = MBrace.Connect("192.168.0.40", port = 2675) // connect to an MBrace runtime
let proc = runtime.CreateProcess <@ lineCount () @> // send computation to the runtime
let lines = proc.AwaitResult () // await completion

(**
## Documentation & Tutorials

A collection of tutorials, technical overviews and API references of the library.

  * [The Programming Model](programming-model.html) An overview of the MBrace programming model.

  * [Azure Tutorial](azure-tutorial.html) Getting started with MBrace on Windows Azure.

  * [API Reference](reference/index.html) contains automatically generated documentation for all types, modules
    and functions in the library.



## Contributing and copyright

The project is hosted on [GitHub][gh] where you can [report issues][issues], fork 
the project and submit pull requests.

The library is available under the Apache License. 
For more information see the [License file][license] in the GitHub repository. 

  [gh]: https://github.com/nessos/MBrace
  [issues]: https://github.com/nessos/MBrace/issues
  [license]: https://github.com/nessos/MBrace/blob/master/License.md
*)