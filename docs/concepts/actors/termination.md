## Explicit termination of an actor

Coyote actors and state machines continue running unless they are explicitly terminated. The runtime
will mark an actor as idle if it has no work to do, but it will not reclaim any resources held by the
actor unless it is terminated. An actor is terminated when it performs the `Halt` operation, as seen
in the following example:

```csharp
private class Example : Actor
{
    private void SomeAction()
    {
        if (this.timeToStop)
        {
            this.Halt();
        }
    }

    protected override Task OnHaltAsync(Event e)
    {
        // Do some cleanup on halt.
        return Task.CompletedTask;
    }
}
```

Additionally, an actor can be halted by another actor by sending a special built-in event called
`HaltEvent`. On state machines this event can also be used for self termination using `RaiseEvent`.
Termination of an actor due to an unhandled `HaltEvent` event is valid behavior (the Coyote runtime
does not report an error). An event sent to a halted actor is simply dropped. A halted actor cannot
be restarted; it remains halted forever.

The Coyote runtime implements actor termination efficiently by cleaning up resources allocated to a
halted actor and recording that the actor has halted.

Actor termination via `Halt` is an asynchronous operation. So in failover scenarios where you need
to be sure an actor is fully terminated before creating it's replacement actor, you will need to
create a handshake callback event sent from `OnHaltAsync` telling the caller that the actor has
officially halted, otherwise there will be a brief period of time where both actors are alive which
may not be what you want when modeling a failover situation. This is shown in the [test
failover](../../tutorials/actors/test-failover.md) tutorial.

### Halting actors externally with await

The `IActorRuntime` interface provides two methods for externally halting actors and awaiting
their full cleanup:

```csharp
// Halt a specific actor and wait for OnHaltAsync to complete.
await runtime.HaltActorAsync(actorId);

// Halt all currently active actors and wait for all OnHaltAsync callbacks to complete.
await runtime.HaltAllActorsAsync();
```

`HaltActorAsync` sends a `HaltEvent` to the actor with the specified id and returns a task that
completes when the actor has fully halted (after `OnHaltAsync` completes). If the actor is already
halted or does not exist, the returned task completes immediately.

`HaltAllActorsAsync` sends a `HaltEvent` to all currently active actors and returns a task that
completes when all actors have fully halted. Actors created after this call begins are not affected.

These methods eliminate the need for manual handshake callbacks in failover scenarios. Instead of
implementing a custom "I'm halted" event from `OnHaltAsync`, you can simply `await` the halt:

```csharp
// Old approach: required a custom handshake event.
runtime.SendEvent(actorId, HaltEvent.Instance);
// ... needed a callback to know when halting completed.

// New approach: just await the halt.
await runtime.HaltActorAsync(actorId);
// Actor is now fully halted, safe to create replacement.
var replacement = runtime.CreateActor(typeof(MyActor));
```
