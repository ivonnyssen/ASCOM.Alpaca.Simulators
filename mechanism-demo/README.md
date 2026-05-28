# Mechanism demo — rusty-photon issue #326

Standalone, offline reproduction of the OmniSim `TelescopeHardware` slew-state
race that makes rusty-photon's `center_on_target` BDD scenarios hang
(<https://github.com/ivonnyssen/rusty_photon/issues/326>).

`Program.cs` is a **faithful model** of the slew state machine in
`TelescopeSimulator/TelescopeHardware.cs` (the relevant line numbers are cited
inline). It needs no NuGet packages and no OmniSim runtime — pure BCL.

```sh
dotnet run -c Release    # exit 0 == every scenario matched the #326 theory
```

It demonstrates three scenarios:

- **A. Consistent start** — `slewing==true` observed *together with*
  `SlewState==SlewRaDec`. Completes normally.
- **B. Race-exposed observation** — `SlewState==SlewRaDec` while the timer
  thread's view of `slewing` is a stale `false`. This is a **permanent wedge**:
  the mount never moves and `IsSlewing` stays `true` forever — the issue's
  verbatim symptom.
- **C. The fix** — once `slewing` is observed coherently (the memory barrier the
  `hardwareLock` patch provides), the wedge clears immediately.

The only difference between A (works) and B (wedges forever) is whether the
timer thread observed `slewing` consistently with `SlewState`. The unordered
writes in `StartSlewAxes` (`targetAxes` → `SlewState` → `slewing`, no barrier)
permit observation B on a weak-memory architecture (AArch64) or under JIT
register-caching; the `hardwareLock` patch on the writers and the timer tick
closes that window.

> This is a deterministic proof of the **state-machine defect and the fix**. It
> does not reproduce the underlying weak-memory stale read live — that is the
> intermittent part (and the reason it surfaces as a CI flake). Reachability of
> scenario B rests on the documented CI failures in #326 plus the standard
> memory-model argument.
