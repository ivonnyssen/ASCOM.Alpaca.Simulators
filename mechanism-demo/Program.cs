// Mechanism demo for rusty-photon issue #326.
//
// This is a FAITHFUL MODEL of the slew state machine in OmniSim's
// TelescopeSimulator/TelescopeHardware.cs (fork: ivonnyssen/ASCOM.Alpaca.Simulators).
// The logic below is copied from the cited line numbers; nothing about the
// state transitions is invented. It runs fully offline (pure BCL, no NuGet).
//
// What it proves, deterministically:
//   A. A *consistent* slew start (slewing==true WITH SlewState==SlewRaDec) — the
//      state the hardwareLock guarantees — completes normally.
//   B. The race-exposed observation (SlewState==SlewRaDec WITH slewing==false) is a
//      PERMANENT, unrecoverable wedge: the mount never moves and IsSlewing stays
//      true forever. This is the issue's verbatim symptom.
//   C. Publishing the consistent value of `slewing` (what the lock's barrier
//      guarantees the timer thread observes) immediately cures the wedge.
//
// The only difference between A (works) and B (wedges) is whether the timer thread
// observed `slewing` consistently with `SlewState`. The unordered writes in
// StartSlewAxes (TelescopeHardware.cs:1696-1703 — `SlewState=` then `slewing=`, no
// barrier) plus weak memory / JIT register-caching (issue race #2) are what let the
// timer observe B; `lock (hardwareLock)` on both the writers and the timer tick
// removes that possibility.

using System;

enum SlewType { SlewNone, SlewRaDec, SlewAltAz, SlewSettle, SlewPark, SlewHome }

// Faithful model of the relevant TelescopeHardware static state + methods.
sealed class Mount
{
    // Shared static fields in the real code (all plain, unsynchronised).
    public bool slewing;            // TelescopeHardware.cs:271
    public SlewType SlewState;      // :1440 (auto-property in the real code)
    public double mountAxis;        // :260 (primary axis; modelled 1-D — the wedge is axis-independent)
    public double targetAxis;       // :265
    public DateTime settleTime;
    public double slewSettleSeconds = 0.0;

    const double slewSpeedSlow = 1.0; // stand-in for SharedResources slew speed; exact value is irrelevant to the wedge

    // StartSlewAxes(Vector, SlewType) — TelescopeHardware.cs:1696-1703.
    // The real code does these three stores with NO barrier between them.
    public void StartSlewAxes(double target, SlewType st)
    {
        targetAxis = target;   // store 1
        SlewState = st;        // store 2 (becomes non-None)
        slewing = true;        // store 3
    }

    // DoSlew() — TelescopeHardware.cs:1812-1922. `timerViewOfSlewing` is what the
    // TIMER THREAD observes for `slewing` at this tick (the real bail is `if
    // (!slewing) return;` at :1815, reading the shared field through the timer
    // thread's cache).
    double DoSlew(bool timerViewOfSlewing)
    {
        double change = 0;
        if (!timerViewOfSlewing) return change;   // :1815 — stale-false bail

        bool finished = true;
        double delta = targetAxis - mountAxis;
        if (Math.Abs(delta) < slewSpeedSlow) change = delta;            // snap to target
        else { change = Math.Sign(delta) * slewSpeedSlow; finished = false; }

        if (finished)
        {
            slewing = false;                       // :1886
            switch (SlewState)                     // :1887-1918
            {
                case SlewType.SlewRaDec:
                case SlewType.SlewAltAz:
                    SlewState = SlewType.SlewSettle;
                    settleTime = DateTime.Now.AddSeconds(slewSettleSeconds);
                    break;
                case SlewType.SlewPark:
                case SlewType.SlewHome:
                    SlewState = SlewType.SlewNone;
                    break;
                case SlewType.SlewNone:
                    break;
                default:
                    SlewState = SlewType.SlewNone;
                    break;
            }
        }
        return change;
    }

    // The tail of MoveAxes() — TelescopeHardware.cs:822-841. Applies DoSlew's change,
    // then runs the SlewState switch that ONLY has a case for SlewSettle. There is no
    // case for SlewRaDec/SlewAltAz here — those are advanced inside DoSlew, which
    // bails when the timer's view of `slewing` is stale-false.
    public void Tick(bool timerViewOfSlewing)
    {
        double change = DoSlew(timerViewOfSlewing);
        mountAxis += change;
        switch (SlewState)               // :832-841
        {
            case SlewType.SlewSettle:
                if (DateTime.Now >= settleTime) SlewState = SlewType.SlewNone;
                break;
            // no SlewRaDec / SlewAltAz case
        }
    }

    // IsSlewing — TelescopeHardware.cs:1590-1604 (what Alpaca `Slewing` returns; the
    // exact property rp polls in poll_slewing_until_idle).
    public bool IsSlewing => SlewState != SlewType.SlewNone || slewing;
}

static class Program
{
    static int Main()
    {
        int failures = 0;
        Console.WriteLine("rusty-photon #326 — TelescopeHardware slew-state mechanism demo");
        Console.WriteLine("(faithful model of TelescopeSimulator/TelescopeHardware.cs)\n");

        // ---- Scenario A: consistent start (what hardwareLock guarantees) ----
        {
            var m = new Mount { mountAxis = 0, targetAxis = 5 };
            // Consistent: SlewState and slewing set together, both observed coherently.
            m.StartSlewAxes(5, SlewType.SlewRaDec);
            bool ok = false;
            for (int i = 0; i < 1000; i++)
            {
                m.Tick(timerViewOfSlewing: m.slewing); // timer observes the true, coherent value
                if (!m.IsSlewing) { ok = true; break; }
            }
            Console.WriteLine($"A. Consistent start (slewing=true WITH SlewState=RaDec):");
            Console.WriteLine($"     mountAxis={m.mountAxis:0.0} (target 5), SlewState={m.SlewState}, IsSlewing={m.IsSlewing}");
            Console.WriteLine($"     => {(ok && Math.Abs(m.mountAxis - 5) < 1e-9 ? "COMPLETES normally ✓" : "did NOT complete ✗")}\n");
            if (!(ok && Math.Abs(m.mountAxis - 5) < 1e-9)) failures++;
        }

        // ---- Scenario B: race-exposed observation (the bug) ----
        {
            var m = new Mount { mountAxis = 0, targetAxis = 5 };
            // A real slew start: the REQUEST thread publishes slewing=true AND
            // SlewState=RaDec (shared `slewing` is genuinely true).
            m.StartSlewAxes(5, SlewType.SlewRaDec);
            // But the TIMER thread's view of `slewing` is stale-false: store-3 from
            // StartSlewAxes (:1666) is not yet visible to it / the JIT kept it in a
            // register across the tick (issue race #2). It keeps seeing the stale value.
            bool everCleared = false;
            for (int i = 0; i < 100_000; i++)
            {
                m.Tick(timerViewOfSlewing: false); // stale-false view persists
                if (!m.IsSlewing) { everCleared = true; break; }
            }
            bool wedged = !everCleared && Math.Abs(m.mountAxis - 0) < 1e-9 && m.IsSlewing;
            Console.WriteLine($"B. Race-exposed observation (SlewState=RaDec WITH slewing=false), 100k ticks:");
            Console.WriteLine($"     mountAxis={m.mountAxis:0.0} (target 5 — never moved), SlewState={m.SlewState}, IsSlewing={m.IsSlewing}");
            Console.WriteLine($"     => {(wedged ? "PERMANENT WEDGE — IsSlewing stuck true forever ✗ (matches issue symptom)" : "recovered ✓")}\n");
            if (!wedged) failures++; // we EXPECT the wedge here; absence would contradict the theory
        }

        // ---- Scenario C: the fix — publishing the consistent `slewing` cures it ----
        {
            var m = new Mount { mountAxis = 0, targetAxis = 5 };
            m.StartSlewAxes(5, SlewType.SlewRaDec);  // shared slewing=true, as in B
            for (int i = 0; i < 10; i++) m.Tick(timerViewOfSlewing: false); // stale view => wedged
            bool wedgedFirst = m.IsSlewing && Math.Abs(m.mountAxis) < 1e-9;
            // hardwareLock's barrier makes the timer observe the TRUE value of slewing.
            bool ok = false;
            for (int i = 0; i < 1000; i++)
            {
                m.Tick(timerViewOfSlewing: m.slewing); // coherent read (post-barrier)
                if (!m.IsSlewing) { ok = true; break; }
            }
            Console.WriteLine($"C. Fix — timer observes `slewing` coherently (the lock's barrier):");
            Console.WriteLine($"     wedged before barrier={wedgedFirst}, then mountAxis={m.mountAxis:0.0} (target 5), IsSlewing={m.IsSlewing}");
            Console.WriteLine($"     => {(wedgedFirst && ok ? "WEDGE CLEARS once slewing is observed consistently ✓" : "unexpected ✗")}\n");
            if (!(wedgedFirst && ok)) failures++;
        }

        Console.WriteLine(failures == 0
            ? "RESULT: all scenarios behaved as the #326 theory predicts."
            : $"RESULT: {failures} scenario(s) did NOT match the theory.");
        return failures;
    }
}
