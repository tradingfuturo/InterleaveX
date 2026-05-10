# InterleaveX Samples

These samples were inherited from Microsoft Coyote and demonstrate
InterleaveX's capabilities. The API surface is unchanged from upstream, so the
sample code itself works against either `Microsoft.Coyote.*` or `InterleaveX.*`
packages — only the package references differ.

This directory contains two sets of samples.

The first set of samples shows how you can use Coyote to systematically test unmodified C#
task-based applications and services:

- [AccountManager](./AccountManager): demonstrates how to write a simple task-based C# application
  to create, get and delete account records in a backend NoSQL database and then systematically test
  this application using Coyote to find a race condition.
- [ImageGalleryAspNet](./WebApps/ImageGalleryAspNet): demonstrates how to use Coyote to test an ASP.NET Core
  service.
- [Coffee Machine Failover](./CoffeeMachineTasks): demonstrates how to systematically test
  the failover logic in your task-based applications.
- [BoundedBuffer](./BoundedBuffer): demonstrates how to use `coyote rewrite` to find deadlocks in
  unmodified C# code.

The second set of samples shows how you can use the Coyote
[actor](https://microsoft.github.io/coyote/concepts/actors/overview/) programming model
to build reliable applications and services:

- [HelloWorldActors](./HelloWorldActors): demonstrates how to write a simple Coyote application
  using actors, and then run and systematically test it.
- [CloudMessaging](./CloudMessaging): demonstrates how to write a Coyote application that contains
  components that communicate with each other using the [Azure Service
  Bus](https://azure.microsoft.com/en-us/services/service-bus/) cloud messaging queue.
- [Coffee Machine Failover](./CoffeeMachineActors): demonstrates how to systematically test
  the failover logic in your Coyote actor applications.
- [Robot Navigator Failover](./DrinksServingRobotActors): demonstrates how to
  systematically test the failover logic in your Coyote actors applications.
- [Timers in Actors](./Timers): demonstrates how to use the timer API of the Coyote actor
  programming model.

## Get started

To build and run the samples, you will need to:

- Install the [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet) (or newer).
- Install the InterleaveX CLI tool: `dotnet tool install --global InterleaveX.CLI`.
  (Installation guidance inherited from the upstream Microsoft Coyote
  [docs](https://microsoft.github.io/coyote/get-started/install/) applies; only
  the package and command names differ.)

Once you are ready, build the samples by running the following script from the root of the
repository in `powershell`:
```
./Samples/Scripts/build.ps1
```

You can find the compiled binaries in the `bin` directory. Use the `interleavex` tool to
automatically test these samples and find bugs. First, read how to use the tool
[here](../get-started/using-coyote.md). Then, follow the instructions in each sample.

## Using the local packages

By default, the samples reference the published `Microsoft.Coyote` NuGet
packages (the original upstream packages). The samples have not yet been
migrated to consume `InterleaveX.*` packages — this is tracked as a follow-up.
If you want to use locally built binaries, run the following script in
`powershell`:

```
./Samples/Scripts/build.ps1 -local
```

To use locally built NuGet packages:

```
./Samples/Scripts/build.ps1 -local -nuget
```
