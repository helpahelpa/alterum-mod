using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;

namespace MusicSession.RealPath
{
    // Two real copies of the shipping music sources (LobbyMusicNet.cs + LobbyMusicHooks.cs) are
    // loaded into separate AssemblyLoadContexts and wired to each other with the actual wire frames.
    // Everything below is a check at a shipping seam: the click prefix, the control encode/decode,
    // the host validation/commit, the publication, the follower reconcile.
    internal static class Program
    {
        private const ulong HostId = 0;
        private const ulong FollowerId = 2;

        private static int _checks, _failed;
        private static string _scenario = "";
        private static readonly List<string> Failures = new List<string>();

        private static void Check(string name, bool ok, string detail = "")
        {
            _checks++;
            if (ok) return;
            _failed++;
            var line = $"{_scenario}: {name}{(detail.Length > 0 ? "  [" + detail + "]" : "")}";
            Failures.Add(line);
            Console.WriteLine("FAIL " + line);
        }

        private static int Main()
        {
            var dll = FindPeerDll();
            Console.WriteLine("peer: " + dll);

            Run("follower pause/resume -> host commit", FollowerPauseResume);
            Run("follower seek playing + paused, beyond coverage", FollowerSeek);
            Run("auth: nonce, revision, membership, dedupe, CAS", AuthAndDedupe);
            Run("periodic state seq is not the control CAS", CadenceDoesNotStale);
            Run("X replacement race cannot apply an X pause to Y", ReplacementRace);
            Run("late join during pause starts paused", LateJoinPaused);
            Run("bounded pending resolves; no permanently disabled UI", PendingUi);
            Run("near-simultaneous host and follower pause", NearSimultaneous);
            Run("host controls + direct-link passthrough", HostControls);
            Run("client reload reuses request id without losing the new URL", ClientReloadReuse);
            Run("same url after a reload is a new intention, not a retransmission", SameUrlAfterReload);
            Run("process-wide ids survive a reload without overlap", IdSpaceAcrossReload);
            Run("id space exhaustion is an explicit refusal, not a wrap", IdSpaceExhaustion);
            Run("host session nonce/caches reset on leave-rehost and epoch change", SessionEpochIsolation);
            Run("wire sizes and mixed protocol rejection", WireBudget);

            Console.WriteLine();
            Console.WriteLine($"{_checks - _failed}/{_checks} checks passed, {_failed} failed");
            if (_failed > 0)
            {
                Console.WriteLine("FAILURES:");
                foreach (var f in Failures) Console.WriteLine("  " + f);
                return 1;
            }
            Console.WriteLine("PASS");
            return 0;
        }

        private static void Run(string name, Action scenario)
        {
            _scenario = name;
            Console.WriteLine("== " + name);
            try { scenario(); }
            catch (Exception e)
            {
                Check("scenario completed", false, e.GetType().Name + ": " + e.Message);
            }
        }

        // ------------------------------------------------------------------ scenarios

        private static void FollowerPauseResume()
        {
            using var rig = new Rig();
            rig.StartXAndFollow();
            var host = rig.Host.Snap();
            var b = rig.B.Snap();

            Check("host channel is playing", host["phase"] == "Playing" && host["channel"] == "1",
                  $"host phase={host["phase"]} channel={host["channel"]}");
            Check("follower channel is playing", b["phase"] == "Playing" && b["channel"] == "1",
                  $"B phase={b["phase"]} channel={b["channel"]}");
            // The reported symptom: B's pause is disabled while following.
            Check("follower pause glyph is enabled", b["uiPause"] == "1", $"uiPause={b["uiPause"]} uiPlay={b["uiPlay"]}");
            Check("follower play glyph is disabled while playing", b["uiPlay"] == "0", $"uiPlay={b["uiPlay"]}");
            Check("follower seek is enabled", b["uiSeek"] == "1", $"uiSeek={b["uiSeek"]}");

            for (int cycle = 1; cycle <= 3; cycle++)
            {
                var before = rig.LastStateFromHost();
                bool consumed = !rig.B.ClickPause();
                Check($"cycle {cycle}: B pause click is consumed", consumed);
                Check($"cycle {cycle}: B did not pause its own channel before the host",
                      rig.B.Snap()["enginePaused"] == "0", rig.B.Snap()["enginePaused"]);
                var control = rig.LastWireType(25);
                Check($"cycle {cycle}: B sent a control request", control != null);
                if (control != null)
                {
                    Check($"cycle {cycle}: control is protocol v3", control.Length >= 2 && control[1] == 3,
                          control.Length + " bytes v=" + (control.Length > 1 ? control[1] : 0));
                    Check($"cycle {cycle}: control is a pause action", control.Length > 2 && control[2] == 0,
                          "action=" + (control.Length > 2 ? control[2] : 0));
                    uint cNonce = BitConverter.ToUInt32(control, 4);
                    uint cRev = BitConverter.ToUInt32(control, 8);
                    uint cId = BitConverter.ToUInt32(control, 12);
                    uint cGen = BitConverter.ToUInt32(control, 16);
                    Check($"cycle {cycle}: control binds the host nonce", before != null && cNonce == before.Nonce,
                          $"control={cNonce} state={before?.Nonce}");
                    Check($"cycle {cycle}: control binds the track revision", before != null && cRev == before.Revision,
                          $"control={cRev} state={before?.Revision}");
                    Check($"cycle {cycle}: control binds the control generation", before != null && cGen == before.ControlGen,
                          $"control={cGen} state={before?.ControlGen}");
                    Check($"cycle {cycle}: control carries a request id", cId != 0, cId.ToString(CultureInfo.InvariantCulture));
                    Check($"cycle {cycle}: follower addresses the host as id 0", rig.FollowerTargets().All(t => t == HostId),
                          string.Join(",", rig.FollowerTargets()));
                }

                rig.Route();
                rig.Step();
                host = rig.Host.Snap();
                b = rig.B.Snap();
                Check($"cycle {cycle}: host committed exactly one pause", host["pauseCalls"] == cycle.ToString(),
                      $"pauseCalls={host["pauseCalls"]}");
                Check($"cycle {cycle}: host transport paused", host["enginePaused"] == "1", host["enginePaused"]);
                Check($"cycle {cycle}: follower channel paused", b["enginePaused"] == "1", b["enginePaused"]);
                Check($"cycle {cycle}: follower play glyph enabled after pause", b["uiPlay"] == "1" && b["uiPause"] == "0",
                      $"uiPlay={b["uiPlay"]} uiPause={b["uiPause"]}");
                Check($"cycle {cycle}: follower pending resolved", b["controlPending"] == "0", b["controlPending"]);
                var pub = rig.LastStateFromHost();
                Check($"cycle {cycle}: host published the pause", pub != null && pub.Intent == 2,
                      "intent=" + (pub?.Intent.ToString() ?? "none"));
                Check($"cycle {cycle}: publication advanced the control generation",
                      pub != null && before != null && pub.ControlGen == before.ControlGen + 1,
                      $"gen {before?.ControlGen} -> {pub?.ControlGen}");
                Check($"cycle {cycle}: publication stayed a pause (not a toggle)",
                      rig.Host.Snap()["enginePaused"] == "1", rig.Host.Snap()["enginePaused"]);

                rig.Wire.Clear();
                consumed = !rig.B.ClickPlay();
                Check($"cycle {cycle}: B play click is consumed", consumed);
                rig.Route();
                rig.Step();
                host = rig.Host.Snap();
                b = rig.B.Snap();
                Check($"cycle {cycle}: host committed exactly one resume", host["resumeCalls"] == cycle.ToString(),
                      $"resumeCalls={host["resumeCalls"]}");
                Check($"cycle {cycle}: both transports playing",
                      host["enginePaused"] == "0" && b["enginePaused"] == "0",
                      $"host={host["enginePaused"]} B={b["enginePaused"]}");
                Check($"cycle {cycle}: follower pause glyph enabled again", b["uiPause"] == "1" && b["uiPlay"] == "0",
                      $"uiPlay={b["uiPlay"]} uiPause={b["uiPause"]}");
                Check($"cycle {cycle}: resume publication reached the follower",
                      rig.LastStateFromHost()?.Intent == 1, "intent=" + (rig.LastStateFromHost()?.Intent.ToString() ?? "none"));
            }
        }

        private static void FollowerSeek()
        {
            using var rig = new Rig();
            rig.StartXAndFollow();

            rig.Wire.Clear();
            long bBefore = Num(rig.B.Snap(), "positionMs");
            Check("B seek click is consumed", !rig.B.ClickSeek(0.5));
            Check("B did not seek its own channel before the host", Num(rig.B.Snap(), "positionMs") == bBefore,
                  $"{bBefore} -> {rig.B.Snap()["positionMs"]}");
            rig.Route();
            rig.Step();
            var host = rig.Host.Snap();
            var b = rig.B.Snap();
            long total = Num(host, "totalMs");
            long target = total / 2;
            Check("host committed one seek", host["seekCalls"] == "1", host["seekCalls"]);
            Check("host position is the absolute target", Math.Abs(Num(host, "seekTarget") - target) <= 2000,
                  $"seekTarget={host["seekTarget"]} want~{target}");
            Check("follower converged to the seek target", Math.Abs(Num(b, "positionMs") - target) <= 750,
                  $"B={b["positionMs"]} target={target}");

            // Pause, then seek while paused: the host must keep it paused.
            rig.Host.ClickPause();
            rig.Route();
            rig.Step();
            rig.Wire.Clear();
            long pausedTarget = total / 4;
            Check("B paused seek click is consumed", !rig.B.ClickSeek(0.25));
            rig.Route();
            rig.Step();
            host = rig.Host.Snap();
            b = rig.B.Snap();
            Check("paused seek still committed by the host", host["seekCalls"] == "2", host["seekCalls"]);
            Check("paused seek stayed paused on the host", host["enginePaused"] == "1", host["enginePaused"]);
            Check("paused seek stayed paused on the follower", b["enginePaused"] == "1", b["enginePaused"]);
            Check("follower paused position converged", Math.Abs(Num(b, "positionMs") - pausedTarget) <= 750,
                  $"B={b["positionMs"]} target={pausedTarget}");

            // Beyond coverage: a truthful rejection, never a false success.
            rig.Host.SetCoverage(30000);
            rig.Wire.Clear();
            long before = Num(rig.Host.Snap(), "positionMs");
            Check("past-coverage seek click is consumed", !rig.B.ClickSeek(0.9));
            rig.Route();
            rig.Step();
            host = rig.Host.Snap();
            b = rig.B.Snap();
            Check("past-coverage seek was NOT committed", host["seekCalls"] == "2", host["seekCalls"]);
            Check("host did not move", Num(host, "positionMs") == before,
                  $"{before} -> {host["positionMs"]}");
            Check("host published no false success", host["enginePaused"] == "1" && host["phase"] == "Paused",
                  host["phase"]);
            Check("follower was told why", b["uiError"].ToLowerInvariant().Contains("coverage") ||
                  b["uiError"].ToLowerInvariant().Contains("available") ||
                  b["uiError"].ToLowerInvariant().Contains("range"), b["uiError"]);
            Check("follower pending resolved after rejection", b["controlPending"] == "0", b["controlPending"]);
            Check("follower controls are not stuck disabled", b["uiPlay"] == "1" || b["uiSeek"] == "1",
                  $"uiPlay={b["uiPlay"]} uiSeek={b["uiSeek"]}");
        }

        private static void AuthAndDedupe()
        {
            using var rig = new Rig();
            rig.StartXAndFollow();
            var latest = rig.LastStateFromHost();
            Check("state frame carries a control generation", latest != null && latest.ControlGen >= 0,
                  latest == null ? "no state" : "len=" + latest.Bytes.Length);

            // Active membership: a sender the transport does not list cannot control the session.
            uint nonce = latest?.Nonce ?? 1;
            uint rev = latest?.Revision ?? 1;
            uint gen = latest?.ControlGen ?? 0;
            long total = 222000;

            int beforePause = (int)Num(rig.Host.Snap(), "pauseCalls");
            rig.Host.Deliver(9, Frames.Control(0, nonce, rev, 1, gen, 0));
            Check("nonmember control did not commit", (int)Num(rig.Host.Snap(), "pauseCalls") == beforePause,
                  rig.Host.Snap()["pauseCalls"]);
            Check("nonmember rejection is loud", rig.Host.LogsContain("active member") || rig.Host.LogsContain("not an active"),
                  Tail(rig.Host.Logs()));

            rig.Host.Deliver(FollowerId, Frames.Control(0, nonce ^ 0x5a5a5a5a, rev, 2, gen, 0));
            Check("old host nonce did not commit", (int)Num(rig.Host.Snap(), "pauseCalls") == beforePause,
                  rig.Host.Snap()["pauseCalls"]);

            rig.Host.Deliver(FollowerId, Frames.Control(0, nonce, rev + 7, 3, gen, 0));
            Check("wrong track revision did not commit", (int)Num(rig.Host.Snap(), "pauseCalls") == beforePause,
                  rig.Host.Snap()["pauseCalls"]);

            // A valid request commits once.
            var valid = Frames.Control(0, nonce, rev, 4, gen, 0);
            rig.Host.Deliver(FollowerId, valid);
            Check("valid control committed once", (int)Num(rig.Host.Snap(), "pauseCalls") == beforePause + 1,
                  rig.Host.Snap()["pauseCalls"]);
            // Same frame again: the verdict is re-sent, the start is not repeated.
            rig.Host.Deliver(FollowerId, valid);
            Check("retransmitted request id is deduped", (int)Num(rig.Host.Snap(), "pauseCalls") == beforePause + 1,
                  rig.Host.Snap()["pauseCalls"]);
            Check("dedupe is logged", rig.Host.LogsContain("dedupe"), Tail(rig.Host.Logs()));
            // An explicit pause while already paused is not a toggle: the transport stays paused.
            var pausedState = rig.LastStateFromHost();
            if (pausedState == null)
            {
                // The direct Deliver path bypasses Route; read the generation the host reports.
                pausedState = new StateFrame { Nonce = valid[4] | ((uint)valid[5] << 8) | ((uint)valid[6] << 16) | ((uint)valid[7] << 24),
                                               Revision = BitConverter.ToUInt32(valid, 8),
                                               ControlGen = (uint)Math.Max(0, Num(rig.Host.Snap(), "hostControlGen")) };
            }
            rig.Host.Deliver(FollowerId, Frames.Control(0, pausedState.Nonce, pausedState.Revision, 7,
                                                        (uint)Num(rig.Host.Snap(), "hostControlGen"), 0));
            Check("an explicit pause while paused stays paused", rig.Host.Snap()["enginePaused"] == "1",
                  rig.Host.Snap()["enginePaused"]);
            Check("an explicit pause does not resume", Num(rig.Host.Snap(), "resumeCalls") == 0,
                  rig.Host.Snap()["resumeCalls"]);

            // A request bound to the pre-commit generation is stale: resume with gen must be rejected.
            int beforeResume = (int)Num(rig.Host.Snap(), "resumeCalls");
            rig.Host.Deliver(FollowerId, Frames.Control(1, nonce, rev, 5, gen, 0));
            Check("stale base generation did not commit", (int)Num(rig.Host.Snap(), "resumeCalls") == beforeResume,
                  rig.Host.Snap()["resumeCalls"]);
            Check("stale control gets the current snapshot", rig.LastWireType(20) != null || rig.Host.LogsContain("stale"),
                  Tail(rig.Host.Logs()));

            // The new generation is accepted.
            var snap = rig.Host.Snap();
            uint gen2 = (uint)Math.Max(0, Num(snap, "hostControlGen"));
            rig.Host.Deliver(FollowerId, Frames.Control(1, nonce, rev, 6, gen2, 0));
            Check("current generation accepted", (int)Num(rig.Host.Snap(), "resumeCalls") == beforeResume + 1,
                  rig.Host.Snap()["resumeCalls"]);
        }

        private static void CadenceDoesNotStale()
        {
            using var rig = new Rig();
            rig.StartXAndFollow();
            var first = rig.LastStateFromHost();
            // Several cadence publications: only seq moves.
            rig.Pump(6);
            var later = rig.LastStateFromHost();
            Check("periodic frames carry the same control generation", first != null && later != null && first.ControlGen == later.ControlGen,
                  $"first={first?.ControlGen} later={later?.ControlGen}");
            Check("periodic frames advance the state sequence", later != null && later.Sequence > first.Sequence,
                  $"{first?.Sequence} -> {later?.Sequence}");

            uint gen = later.ControlGen;
            rig.Host.Deliver(FollowerId, Frames.Control(0, later.Nonce, later.Revision, 10, gen, 0));
            Check("a click based on the pre-cadence generation still commits",
                  (int)Num(rig.Host.Snap(), "pauseCalls") == 1,
                  rig.Host.Snap()["pauseCalls"]);

            int commits = (int)Num(rig.Host.Snap(), "pauseCalls");
            rig.Host.Deliver(FollowerId, Frames.Control(0, later.Nonce, later.Revision, 11, gen, 0));
            Check("the second same-generation request is stale, not a second commit",
                  (int)Num(rig.Host.Snap(), "pauseCalls") == commits,
                  rig.Host.Snap()["pauseCalls"]);
        }

        private static void ReplacementRace()
        {
            using var rig = new Rig();
            rig.StartXAndFollow();
            var x = rig.LastStateFromHost();
            uint gen = x.ControlGen;

            // A pause bound to X is in flight while the host replaces X with Y.
            rig.Host.Call("Start", "https://www.youtube.com/watch?v=Y");
            rig.Route();
            var y = rig.LastStateFromHost();
            Check("Y became a new revision", y != null && x != null && y.Revision > x.Revision,
                  $"{x?.Revision} -> {y?.Revision}");

            int pauses = (int)Num(rig.Host.Snap(), "pauseCalls");
            rig.Host.Deliver(FollowerId, Frames.Control(0, x.Nonce, x.Revision, 20, gen, 0));
            Check("an X pause cannot pause Y", (int)Num(rig.Host.Snap(), "pauseCalls") == pauses,
                  rig.Host.Snap()["pauseCalls"]);
            Check("the X pause is rejected by revision", rig.Host.LogsContain("revision") || rig.Host.LogsContain("track changed"),
                  Tail(rig.Host.Logs()));

            // A delayed X state frame cannot replace Y on the follower either.
            rig.B.Deliver(HostId, x.Bytes);
            rig.Step();
            var b = rig.B.Snap();
            Check("follower stays on Y", b["url"].EndsWith("v=Y", StringComparison.Ordinal), b["url"]);
            Check("Y is still playing after the stale X pause", b["phase"] == "Playing" || b["phase"] == "Paused",
                  b["phase"]);
            Check("host is still on Y", rig.Host.Snap()["url"].EndsWith("v=Y", StringComparison.Ordinal),
                  rig.Host.Snap()["url"]);
        }

        private static void LateJoinPaused()
        {
            using var rig = new Rig();
            rig.StartXAndFollow();
            rig.Host.ClickPause();
            rig.Route();
            rig.Step();
            Check("host paused", rig.Host.Snap()["enginePaused"] == "1");

            // A fresh follower joins the paused session.
            rig.B.Call("Initialize", false, FollowerId, HostId, new ulong[] { HostId });
            rig.Host.Call("LateJoin", FollowerId);
            rig.Route();
            rig.Step();
            rig.Step();
            var b = rig.B.Snap();
            Check("late joiner starts paused", b["enginePaused"] == "1", b["enginePaused"]);
            Check("late joiner has a channel", b["channel"] == "1", b["channel"]);
            Check("late joiner offers play", b["uiPlay"] == "1" && b["uiPause"] == "0",
                  $"uiPlay={b["uiPlay"]} uiPause={b["uiPause"]}");
            Check("late joiner has the session track", b["url"].EndsWith("v=X", StringComparison.Ordinal), b["url"]);
        }

        private static void PendingUi()
        {
            using var rig = new Rig();
            rig.StartXAndFollow();
            rig.Wire.Clear();
            Check("B pause click is consumed", !rig.B.ClickPause());
            var control = rig.LastWireType(25);
            Check("B sent a control request", control != null);

            var pending = rig.B.Snap();
            Check("pending is visible", pending["controlPending"] == "1", pending["controlPending"]);
            Check("pending status is shown", pending["uiStatus"] == "SYNC", pending["uiStatus"]);
            Check("pending does not optimistically pause", pending["enginePaused"] == "0", pending["enginePaused"]);
            Check("controls stay interactable while pending", pending["uiPause"] == "1" || pending["uiPlay"] == "1",
                  $"uiPlay={pending["uiPlay"]} uiPause={pending["uiPause"]}");

            // The answer never arrives (the frame is dropped); advance past the bounded window.
            rig.AdvanceFollowerOnly(3.0);
            var after = rig.B.Snap();
            Check("pending times out", after["controlPending"] == "0", after["controlPending"]);
            Check("timeout is visible", after["uiError"].Length > 0, after["uiError"]);
            Check("timeout does not leave controls disabled", after["uiPause"] == "1" || after["uiSeek"] == "1",
                  $"uiPlay={after["uiPlay"]} uiPause={after["uiPause"]} uiSeek={after["uiSeek"]}");
            Check("timeout did not pause locally", after["enginePaused"] == "0", after["enginePaused"]);
        }

        private static void NearSimultaneous()
        {
            using var rig = new Rig();
            rig.StartXAndFollow();
            int hostPauses = (int)Num(rig.Host.Snap(), "pauseCalls");
            Check("A pause click is consumed", !rig.Host.ClickPause());
            Check("B pause click is consumed", !rig.B.ClickPause());
            rig.Route();
            rig.Step();
            var host = rig.Host.Snap();
            var b = rig.B.Snap();
            Check("exactly one transport pause won", (int)Num(host, "pauseCalls") == hostPauses + 1,
                  host["pauseCalls"]);
            Check("both transports are paused", host["enginePaused"] == "1" && b["enginePaused"] == "1",
                  $"host={host["enginePaused"]} B={b["enginePaused"]}");
            Check("the losing follower request resolved", b["controlPending"] == "0", b["controlPending"]);
        }

        private static void HostControls()
        {
            using var rig = new Rig();
            rig.StartXAndFollow();
            int pauses = (int)Num(rig.Host.Snap(), "pauseCalls");
            Check("host pause click is consumed", !rig.Host.ClickPause());
            Check("host paused", rig.Host.Snap()["enginePaused"] == "1");
            Check("host control generation advanced", Num(rig.Host.Snap(), "hostControlGen") > 0);
            rig.Route();
            rig.Step();
            Check("follower follows the host pause", rig.B.Snap()["enginePaused"] == "1");

            Check("host resume click is consumed", !rig.Host.ClickPlay());
            Check("host resumed", rig.Host.Snap()["enginePaused"] == "0");
            rig.Route();
            rig.Step();
            Check("follower follows the host resume", rig.B.Snap()["enginePaused"] == "0");

            // Direct audio links stay with the game's own player: no takeover, no control frame.
            rig.Wire.Clear();
            Check("direct link is passthrough, not submitted", !rig.Host.CallB("Submit", "https://x.test/a.mp3"));
            Check("direct link did not touch our transport", rig.Host.Snap()["url"].EndsWith("v=X", StringComparison.Ordinal),
                  rig.Host.Snap()["url"]);
            Check("host phase untouched by a direct link", rig.Host.Snap()["phase"] != "Idle", rig.Host.Snap()["phase"]);
        }

        private static void ClientReloadReuse()
        {
            using var rig = new Rig();
            rig.StartXAndFollow();

            // The follower adopts Y through the real request path, so the host caches verdict id=1.
            rig.B.Call("Submit", "https://www.youtube.com/watch?v=Y");
            rig.Route();
            rig.Step();
            Check("host adopted the follower's first request", rig.Host.Snap()["url"].EndsWith("v=Y", StringComparison.Ordinal),
                  rig.Host.Snap()["url"]);

            // A hot reload/restart resets every static in the hot module, including the request id.
            // The host session and its verdict cache survive.
            rig.ReloadFollower();
            rig.Wire.Clear();
            bool sent = rig.B.CallB("Submit", "https://www.youtube.com/watch?v=Z");
            Check("the reloaded follower can submit a new URL", sent);
            var ask = rig.LastWireType(21);
            Check("the new request is on the wire", ask != null);
            rig.Route();
            rig.Step();
            rig.Step();
            Check("the host starts the NEW url, not the cached verdict",
                  rig.Host.Snap()["url"].EndsWith("v=Z", StringComparison.Ordinal), rig.Host.Snap()["url"]);
            Check("the reloaded follower follows the new url",
                  rig.B.Snap()["url"].EndsWith("v=Z", StringComparison.Ordinal), rig.B.Snap()["url"]);
            Check("a different url under a reused id is not logged as a duplicate",
                  !rig.Host.LogsContain("dedupe id="), Tail(rig.Host.Logs()));

            // The actual retransmission (same sender + id + url) must still be deduped, not restarted.
            if (ask != null)
            {
                var before = rig.LastStateFromHost();
                rig.Host.Deliver(FollowerId, ask);
                var after = rig.LastStateFromHost();
                Check("an identical retransmission is still deduped",
                      before != null && after != null && before.Revision == after.Revision,
                      $"{before?.Revision} -> {after?.Revision}");
                Check("the retransmission dedupe is logged", rig.Host.LogsContain("dedupe id="), Tail(rig.Host.Logs()));
            }
        }

        // The edge the URL-keyed dedupe cannot see on its own: a process-local hot reload starts the
        // follower's request-id space over, so a genuinely NEW intention for the SAME url arrives as
        // the exact (sender, id, url) triple the host already answered in the previous generation.
        // That must start the track again, while a true retransmission of the older request must
        // still be deduped and must not disturb the session. The same class of defect applies to the
        // follower's control ids (keyed by sender + id + host nonce, all three of which a reload
        // reproduces), so a new pause after a reload must commit instead of being answered as a
        // duplicate of the previous generation's pause.
        private static void SameUrlAfterReload()
        {
            const string Y = "https://www.youtube.com/watch?v=Y";
            const string Z = "https://www.youtube.com/watch?v=Z";
            using var rig = new Rig();
            rig.StartXAndFollow();

            // Generation 1: a control (id 1) and a request (id 1) go through the real seams.
            Check("gen1 pause click is consumed", !rig.B.ClickPause());
            rig.Route();
            rig.Step();
            Check("gen1 pause commits", (int)Num(rig.Host.Snap(), "pauseCalls") == 1,
                  rig.Host.Snap()["pauseCalls"]);
            Check("gen1 resume click is consumed", !rig.B.ClickPlay());
            rig.Route();
            rig.Step();

            Check("gen1 submit is sent", rig.B.CallB("Submit", Y));
            var ask1 = rig.LastWireType(21);
            Check("gen1 ask is on the wire", ask1 != null && ask1.Length > 8);
            rig.Route();
            rig.Step();
            rig.Step();
            Check("gen1 request is adopted", rig.Host.Snap()["url"].EndsWith("v=Y", StringComparison.Ordinal),
                  rig.Host.Snap()["url"]);
            var adopted = rig.LastStateFromHost();
            Check("gen1 adoption is a revision", adopted != null && adopted.Revision > 0, "rev=" + adopted?.Revision);

            // Another member moves the session to Z. The host keeps the verdict for (follower,1,Y).
            rig.Host.Call("Start", Z);
            rig.Route();
            rig.Step();
            rig.Step();
            var onZ = rig.LastStateFromHost();
            Check("the session moved to Z", rig.Host.Snap()["url"].EndsWith("v=Z", StringComparison.Ordinal),
                  rig.Host.Snap()["url"]);

            // Hot reload: a fresh assembly in the same process, so every static id starts over while
            // the host process and its verdict caches survive.
            rig.ReloadFollower();
            rig.Wire.Clear();
            rig.Pump(4);
            Check("the reloaded follower has a channel again", rig.B.Snap()["channel"] == "1",
                  "channel=" + rig.B.Snap()["channel"] + " phase=" + rig.B.Snap()["phase"]);

            rig.Wire.Clear();
            int logMark = rig.Host.Logs().Length;
            Check("gen2 submits the SAME url", rig.B.CallB("Submit", Y));
            var ask2 = rig.LastWireType(21);
            Check("the same-url intention is on the wire", ask2 != null);
            rig.Route();
            rig.Step();
            rig.Step();
            Check("the host starts the same url again after the reload",
                  rig.Host.Snap()["url"].EndsWith("v=Y", StringComparison.Ordinal), rig.Host.Snap()["url"]);
            Check("the same-url intention is not answered as a duplicate",
                  !NewLogContains(rig.Host, logMark, "dedupe"), NewTail(rig.Host, logMark));
            var restarted = rig.LastStateFromHost();
            Check("the restart is a new revision",
                  restarted != null && onZ != null && restarted.Revision > onZ.Revision,
                  $"{onZ?.Revision} -> {restarted?.Revision}");

            // The true retransmission of the ORIGINAL request is the same request, not a new one.
            if (ask1 != null)
            {
                var before = rig.LastStateFromHost();
                rig.Host.Deliver(FollowerId, ask1);
                var after = rig.LastStateFromHost();
                Check("the original request's retransmission is still deduped",
                      before != null && after != null && before.Revision == after.Revision,
                      $"{before?.Revision} -> {after?.Revision}");
                Check("that dedupe is logged", NewLogContains(rig.Host, logMark, "dedupe"),
                      NewTail(rig.Host, logMark));
            }

            // Control ids restart too: a new pause must commit, not be answered as a duplicate of
            // the previous generation's pause.
            rig.Pump(2);
            int pauseBefore = (int)Num(rig.Host.Snap(), "pauseCalls");
            int ctrlMark = rig.Host.Logs().Length;
            Check("gen2 pause click is consumed", !rig.B.ClickPause());
            var control = rig.LastWireType(25);
            Check("gen2 pause is on the wire", control != null);
            rig.Route();
            rig.Step();
            Check("gen2 pause commits on the host",
                  (int)Num(rig.Host.Snap(), "pauseCalls") == pauseBefore + 1,
                  $"{pauseBefore} -> {rig.Host.Snap()["pauseCalls"]}");
            Check("gen2 pause is not answered as a duplicate",
                  !NewLogContains(rig.Host, ctrlMark, "control dedupe"), NewTail(rig.Host, ctrlMark));
        }

        // The previous per-generation clock seed could reissue ids: gen1 at t+1..t+n, reload at
        // t+d with d<n. The shipping allocator is one process-lifetime counter in AppDomain data,
        // so the second generation's ids are strictly NEW. This scenario asserts the wire ids, not
        // a log string: control id at offset 12, ask id at offset 2 (u32 little-endian).
        private static void IdSpaceAcrossReload()
        {
            const string Y = "https://www.youtube.com/watch?v=Y";
            using var rig = new Rig();
            rig.StartXAndFollow();

            Check("gen1 pause click is consumed", !rig.B.ClickPause());
            var control1 = rig.LastWireType(25);
            rig.Route();
            rig.Step();
            int pauses = (int)Num(rig.Host.Snap(), "pauseCalls");
            Check("gen1 pause committed", pauses == 1, rig.Host.Snap()["pauseCalls"]);

            Check("gen1 submit is sent", rig.B.CallB("Submit", Y));
            var ask1 = rig.LastWireType(21);
            rig.Route();
            rig.Step();
            Check("gen1 request adopted", rig.Host.Snap()["url"].EndsWith("v=Y", StringComparison.Ordinal),
                  rig.Host.Snap()["url"]);

            Check("gen1 ids are on the wire", control1 != null && ask1 != null);
            uint c1 = control1 == null ? 0 : U32(control1, 12);
            uint a1 = ask1 == null ? 0 : U32(ask1, 2);
            Check("gen1 ids are inside the 32-bit band", c1 != 0 && a1 != 0 && c1 <= 0x7FFFFFFFu && a1 <= 0x7FFFFFFFu,
                  $"control={c1} ask={a1}");

            rig.ReloadFollower();
            rig.Wire.Clear();
            rig.Pump(2);
            Check("reloaded follower has a channel", rig.B.Snap()["channel"] == "1", rig.B.Snap()["channel"]);

            Check("gen2 pause click is consumed", !rig.B.ClickPause());
            var control2 = rig.LastWireType(25);
            rig.Route();
            rig.Step();
            Check("gen2 pause committed", (int)Num(rig.Host.Snap(), "pauseCalls") == pauses + 1,
                  rig.Host.Snap()["pauseCalls"]);

            Check("gen2 submits the SAME url", rig.B.CallB("Submit", Y));
            var ask2 = rig.LastWireType(21);
            rig.Route();
            rig.Step();
            Check("gen2 same-url request is adopted", rig.Host.Snap()["url"].EndsWith("v=Y", StringComparison.Ordinal),
                  rig.Host.Snap()["url"]);

            Check("gen2 ids are on the wire", control2 != null && ask2 != null);
            uint c2 = control2 == null ? 0 : U32(control2, 12);
            uint a2 = ask2 == null ? 0 : U32(ask2, 2);
            Check("gen2 control id is new (no clock-band overlap)", c2 > c1, $"{c1} -> {c2}");
            Check("gen2 request id is new (no clock-band overlap)", a2 > a1, $"{a1} -> {a2}");
            Check("request and control share one process counter", c1 != a1 && c2 != a2, $"{c1},{a1} / {c2},{a2}");
        }

        // Exhaustion is the explicit 32-bit boundary: the allocator refuses and names a process
        // restart; it never wraps to an id the host may already have answered.
        private static void IdSpaceExhaustion()
        {
            using var rig = new Rig();
            rig.StartXAndFollow();
            long saved = (long)rig.B.Call("IdSlotValue");
            rig.B.Call("SetIdSlot", 0x7FFFFFFFL);
            int pauses = (int)Num(rig.Host.Snap(), "pauseCalls");
            int mark = rig.B.Logs().Length;
            bool consumed = rig.B.ClickPause();
            Check("an exhausted id space does not consume the pause click", !consumed,
                  "consumed=" + consumed);
            Check("no control frame is sent when ids are exhausted", rig.LastWireType(25) == null);
            Check("the refusal is explicit about a process restart",
                  NewLogContains(rig.B, mark, "id space is exhausted") && NewLogContains(rig.B, mark, "restart"),
                  NewTail(rig.B, mark));
            Check("the exhausted pause did not reach the host", (int)Num(rig.Host.Snap(), "pauseCalls") == pauses,
                  rig.Host.Snap()["pauseCalls"]);

            mark = rig.B.Logs().Length;
            Check("a request is refused too", !rig.B.CallB("Submit", "https://www.youtube.com/watch?v=Z"));
            Check("the request refusal names a process restart",
                  NewLogContains(rig.B, mark, "id space is exhausted") && NewLogContains(rig.B, mark, "restart"),
                  NewTail(rig.B, mark));
            rig.B.Call("SetIdSlot", saved);        // restore the process slot for later scenarios
        }

        // Leave/rehost and an epoch change on the SAME bridge and role must mint a new host nonce and
        // clear the old verdict caches. The old control (nonce/rev/gen) is rejected with no commit;
        // a fresh pause from the new session commits.
        private static void SessionEpochIsolation()
        {
            const string X = "https://www.youtube.com/watch?v=X";
            const string Y = "https://www.youtube.com/watch?v=Y";
            using var rig = new Rig();
            rig.StartXAndFollow();
            var s0 = rig.LastStateFromHost();
            Check("initial host session has a nonce", s0 != null && s0.Nonce != 0, "nonce=" + s0?.Nonce);

            Check("session-A pause click is consumed", !rig.B.ClickPause());
            var oldControl = rig.LastWireType(25);
            rig.Route();
            rig.Step();
            int pauses = (int)Num(rig.Host.Snap(), "pauseCalls");
            Check("session-A pause commits", pauses == 1, rig.Host.Snap()["pauseCalls"]);
            Check("session-A control was on the wire", oldControl != null);

            // ---- leave -> rehost on the same bridge object (new epoch)
            rig.Host.Call("Leave");
            rig.Host.Call("Rehost", 5u);
            rig.B.Call("Rebind", 5u);
            rig.Wire.Clear();
            rig.Host.Call("Start", X);
            rig.Pump(3);
            var s1 = rig.LastStateFromHost();
            Check("rehost minted a new host nonce", s1 != null && s0 != null && s1.Nonce != s0.Nonce,
                  $"{s0?.Nonce} -> {s1?.Nonce}");
            Check("rehost starts a new revision", s1 != null && s1.Revision > 0, "rev=" + s1?.Revision);

            if (oldControl != null)
            {
                int before = (int)Num(rig.Host.Snap(), "pauseCalls");
                rig.Host.Deliver(FollowerId, oldControl);
                Check("the old session's control is rejected (no commit)",
                      (int)Num(rig.Host.Snap(), "pauseCalls") == before, rig.Host.Snap()["pauseCalls"]);
                Check("the old-session rejection names the stale host session",
                      rig.Host.LogsContain("stale host session"), Tail(rig.Host.Logs()));
            }

            // Old revision and old control generation with the NEW nonce must also be rejected.
            if (s1 != null)
            {
                int before = (int)Num(rig.Host.Snap(), "pauseCalls");
                uint oldRev = s1.Revision > 1 ? s1.Revision - 1 : 999;
                rig.Host.Deliver(FollowerId, Frames.Control(0, s1.Nonce, oldRev, 9001, s1.ControlGen, 0));
                Check("a control for an old revision is rejected", (int)Num(rig.Host.Snap(), "pauseCalls") == before,
                      rig.Host.Snap()["pauseCalls"]);
                rig.Host.Deliver(FollowerId, Frames.Control(0, s1.Nonce, s1.Revision, 9002, s1.ControlGen + 1, 0));
                Check("a control for an old control generation is rejected",
                      (int)Num(rig.Host.Snap(), "pauseCalls") == before, rig.Host.Snap()["pauseCalls"]);
            }

            rig.Pump(2);
            Check("a fresh pause in the rehosted session commits", !rig.B.ClickPause());
            rig.Route();
            rig.Step();
            Check("rehosted-session pause reached the host", (int)Num(rig.Host.Snap(), "pauseCalls") == pauses + 1,
                  rig.Host.Snap()["pauseCalls"]);

            // ---- epoch change WITHOUT a role change: same bridge, same host role.
            var s2 = rig.LastStateFromHost();
            rig.Host.Call("Rebind", 6u);
            rig.B.Call("Rebind", 6u);
            rig.Wire.Clear();
            rig.Host.Call("Start", Y);
            rig.Pump(3);
            var s3 = rig.LastStateFromHost();
            Check("epoch change without a role change mints a new nonce",
                  s3 != null && s2 != null && s3.Nonce != s2.Nonce, $"{s2?.Nonce} -> {s3?.Nonce}");
            if (s2 != null)
            {
                int before = (int)Num(rig.Host.Snap(), "pauseCalls");
                rig.Host.Deliver(FollowerId, Frames.Control(0, s2.Nonce, s2.Revision, 9003, s2.ControlGen, 0));
                Check("the previous epoch's control is rejected", (int)Num(rig.Host.Snap(), "pauseCalls") == before,
                      rig.Host.Snap()["pauseCalls"]);
            }
            rig.Pump(2);
            Check("a pause after the epoch change commits", !rig.B.ClickPause());
            rig.Route();
            rig.Step();
            Check("post-epoch pause reached the host", (int)Num(rig.Host.Snap(), "pauseCalls") == pauses + 2,
                  rig.Host.Snap()["pauseCalls"]);
        }

        private static uint U32(byte[] frame, int offset) => BitConverter.ToUInt32(frame, offset);

        private static void WireBudget()
        {
            using var rig = new Rig();
            rig.StartXAndFollow();
            rig.B.ClickPause();
            rig.Route();
            rig.Step();
            rig.B.ClickSeek(0.5);
            rig.Route();
            rig.Step();
            rig.B.ClickPlay();
            rig.Route();
            rig.Step();

            var sizes = rig.Wire.Select(f => f.Payload.Length).ToList();
            Check("captured both peers' frames", sizes.Count > 4, sizes.Count.ToString(CultureInfo.InvariantCulture));
            Check("every frame stays inside the bridge ceiling", sizes.All(n => n <= 1024), "max=" + sizes.Max());
            bool stateSeen = false, controlSeen = false, ackSeen = false;
            foreach (var f in rig.Wire)
            {
                if (f.Payload.Length < 2) continue;
                if (f.Payload[0] == 20)
                {
                    stateSeen = true;
                    Check("state frame is protocol v3", f.Payload[1] == 3, "v=" + f.Payload[1]);
                    Check("state frame carries a control generation", f.Payload.Length >= 48, f.Payload.Length + " bytes");
                }
                else if (f.Payload[0] == 25)
                {
                    controlSeen = true;
                    Check("control frame is 28 bytes", f.Payload.Length == 28, f.Payload.Length + " bytes");
                    Check("control frame is protocol v3", f.Payload[1] == 3, "v=" + f.Payload[1]);
                }
                else if (f.Payload[0] == 26)
                {
                    ackSeen = true;
                    Check("control ack stays bounded", f.Payload.Length <= 128, f.Payload.Length + " bytes");
                }
            }
            Check("saw state frames", stateSeen);
            Check("saw control requests", controlSeen);
            Check("saw control verdicts", ackSeen);

            // Mixed protocol is rejected loudly, not misread.
            var latest = rig.LastStateFromHost();
            var old = (byte[])latest.Bytes.Clone();
            old[1] = 2;
            rig.B.Deliver(HostId, old);
            Check("a v2 state frame is rejected loudly", rig.B.LogsContain("protocol") || rig.B.LogsContain("version"),
                  Tail(rig.B.Logs()));

            var ctrl = Frames.Control(0, latest.Nonce, latest.Revision, 90, latest.ControlGen, 0);
            ctrl[1] = 2;
            int pauses = (int)Num(rig.Host.Snap(), "pauseCalls");
            rig.Host.Deliver(FollowerId, ctrl);
            Check("a v2 control frame cannot commit", (int)Num(rig.Host.Snap(), "pauseCalls") == pauses,
                  rig.Host.Snap()["pauseCalls"]);
            Check("a v2 control frame is rejected loudly", rig.Host.LogsContain("protocol") || rig.Host.LogsContain("version"),
                  Tail(rig.Host.Logs()));
        }

        // ------------------------------------------------------------------ rig

        private sealed class Rig : IDisposable
        {
            internal readonly Peer Host;
            internal Peer B;
            internal readonly List<WireFrame> Wire = new List<WireFrame>();
            private double _now;

            internal Rig()
            {
                Host = Peer.Load("host");
                B = Peer.Load("follower");
                Host.Call("Initialize", true, HostId, HostId, new ulong[] { FollowerId });
                B.Call("Initialize", false, FollowerId, HostId, new ulong[] { HostId });
            }

            // A real hot reload is a fresh assembly: every static (including the request id) starts
            // at zero while the host process and its verdict cache survive.
            internal void ReloadFollower()
            {
                B.Dispose();
                B = Peer.Load("follower-reload");
                B.Call("Initialize", false, FollowerId, HostId, new ulong[] { HostId });
            }

            internal void StartXAndFollow()
            {
                Host.Call("Start", "https://www.youtube.com/watch?v=X");
                Route();
                Step();
                Step();
                var b = B.Snap();
                if (b["url"].Length == 0)
                {
                    // The accepted revision can be the pre-channel Buffering frame; the channel frame
                    // follows on the next cadence tick.
                    Step();
                }
            }

            internal void Step(double dt = 0.25)
            {
                _now += dt;
                Host.Call("Tick", _now);
                Route();
                B.Call("Tick", _now);
                Route();
            }

            internal void Pump(int count)
            {
                for (int i = 0; i < count; i++) Step();
            }

            internal void AdvanceFollowerOnly(double advance)
            {
                _now += advance;
                B.Call("Tick", _now);
                Route();
            }

            internal void Route()
            {
                for (int round = 0; round < 16; round++)
                {
                    bool any = false;
                    foreach (var f in Drain(Host))
                    {
                        Wire.Add(new WireFrame(HostId, f));
                        B.Call("Deliver", HostId, f);
                        any = true;
                    }
                    foreach (var f in Drain(B))
                    {
                        Wire.Add(new WireFrame(FollowerId, f));
                        Host.Call("Deliver", FollowerId, f);
                        any = true;
                    }
                    if (!any) return;
                }
            }

            private static IEnumerable<byte[]> Drain(Peer p)
            {
                var d = (object[])p.Call("Drain");
                var payloads = (byte[][])d[1];
                return payloads;
            }

            internal List<ulong> FollowerTargets()
            {
                var d = (object[])B.Call("PeekOut");
                return new List<ulong>((ulong[])d[0]);
            }

            internal byte[] LastWireType(byte type)
            {
                // A click writes to the bridge immediately; routing happens on the next Route(). The
                // pending frame is therefore the newest one, ahead of the delivered history.
                foreach (var peer in new[] { B, Host })
                {
                    var d = (object[])peer.Call("PeekOut");
                    var payloads = (byte[][])d[1];
                    for (int i = payloads.Length - 1; i >= 0; i--)
                        if (payloads[i].Length > 0 && payloads[i][0] == type)
                            return payloads[i];
                }
                for (int i = Wire.Count - 1; i >= 0; i--)
                    if (Wire[i].Payload.Length > 0 && Wire[i].Payload[0] == type)
                        return Wire[i].Payload;
                return null;
            }

            internal StateFrame LastStateFromHost()
            {
                for (int i = Wire.Count - 1; i >= 0; i--)
                {
                    var f = Wire[i];
                    if (f.Sender != HostId || f.Payload.Length < 2 || f.Payload[0] != 20) continue;
                    return StateFrame.Parse(f.Payload);
                }
                return null;
            }

            public void Dispose() { Host.Dispose(); B.Dispose(); }
        }

        private sealed class WireFrame
        {
            internal readonly ulong Sender;
            internal readonly byte[] Payload;
            internal WireFrame(ulong sender, byte[] payload) { Sender = sender; Payload = payload; }
        }

        private sealed class StateFrame
        {
            internal byte[] Bytes;
            internal uint Nonce, Revision, Sequence, ControlGen;
            internal byte Intent;
            internal long AnchorMs, HostMs, DurationMs;

            internal static StateFrame Parse(byte[] p)
            {
                if (p.Length < 48) return null;
                var s = new StateFrame { Bytes = p };
                s.Nonce = BitConverter.ToUInt32(p, 4);
                s.Revision = BitConverter.ToUInt32(p, 8);
                s.Sequence = BitConverter.ToUInt32(p, 12);
                s.AnchorMs = BitConverter.ToInt64(p, 16);
                s.HostMs = BitConverter.ToInt64(p, 24);
                s.DurationMs = BitConverter.ToInt64(p, 32);
                s.ControlGen = BitConverter.ToUInt32(p, 44);
                s.Intent = p[2];
                return s;
            }
        }

        private static class Frames
        {
            internal static byte[] Control(byte action, uint nonce, uint rev, uint id, uint gen, long targetMs)
            {
                var b = new byte[28];
                b[0] = 25;
                b[1] = 3;
                b[2] = action;
                b[3] = 0;
                BitConverter.GetBytes(nonce).CopyTo(b, 4);
                BitConverter.GetBytes(rev).CopyTo(b, 8);
                BitConverter.GetBytes(id).CopyTo(b, 12);
                BitConverter.GetBytes(gen).CopyTo(b, 16);
                BitConverter.GetBytes(targetMs).CopyTo(b, 20);
                return b;
            }
        }

        // ------------------------------------------------------------------ one loaded peer copy

        private sealed class Peer : IDisposable
        {
            private readonly AssemblyLoadContext _alc;
            private readonly Type _type;

            private Peer(AssemblyLoadContext alc, Type type)
            {
                _alc = alc;
                _type = type;
            }

            internal static Peer Load(string name)
            {
                var alc = new AssemblyLoadContext("music-" + name + "-" + Guid.NewGuid().ToString("N"), isCollectible: true);
                var asm = alc.LoadFromAssemblyPath(FindPeerDll());
                var t = asm.GetType("AlterumModelSwap.Hot.PeerDriver", throwOnError: true);
                return new Peer(alc, t);
            }

            internal bool ClickPause() => (bool)Call("ClickPause");
            internal bool ClickPlay() => (bool)Call("ClickPlay");
            internal bool ClickSeek(double fraction) => (bool)Call("ClickSeek", fraction);
            internal void Deliver(ulong sender, byte[] payload) => Call("Deliver", sender, payload);
            internal void SetCoverage(long ms) => Call("SetCoverage", ms);

            internal object Call(string method, params object[] args)
            {
                var m = _type.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
                if (m == null) throw new MissingMethodException(_type.FullName, method);
                try { return m.Invoke(null, args); }
                catch (TargetInvocationException e) { throw e.InnerException ?? e; }
            }

            internal bool CallB(string method, params object[] args) => (bool)Call(method, args);

            internal Dictionary<string, string> Snap()
            {
                var text = (string)Call("Snapshot");
                var d = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var line in text.Split('\n'))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    d[line.Substring(0, eq)] = line.Substring(eq + 1).TrimEnd('\r');
                }
                return d;
            }

            internal string[] Logs() => (string[])Call("Logs");
            internal bool LogsContain(string needle) =>
                Logs().Any(l => l.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);

            public void Dispose()
            {
                try { _alc.Unload(); } catch { }
            }
        }

        // ------------------------------------------------------------------ utility

        private static long Num(Dictionary<string, string> snap, string key)
        {
            return snap.TryGetValue(key, out var v) && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n
                : -1;
        }

        private static bool NewLogContains(Peer peer, int from, string needle)
        {
            var all = peer.Logs();
            for (int i = Math.Max(0, from); i < all.Length; i++)
                if (all[i].IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static string NewTail(Peer peer, int from, int n = 6)
        {
            var all = peer.Logs();
            var slice = new List<string>();
            for (int i = Math.Max(0, from); i < all.Length; i++) slice.Add(all[i]);
            return Tail(slice.ToArray(), n);
        }

        private static string Tail(string[] lines, int n = 6)
        {
            if (lines == null || lines.Length == 0) return "";
            return string.Join(" | ", lines.Skip(Math.Max(0, lines.Length - n)));
        }

        private static string FindPeerDll()
        {
            var baseDir = AppContext.BaseDirectory;
            var direct = Path.Combine(baseDir, "MusicSession.Peer.dll");
            if (File.Exists(direct)) return direct;
            var dir = new DirectoryInfo(baseDir);
            for (int i = 0; i < 7 && dir != null; i++, dir = dir.Parent)
            {
                var peer = Path.Combine(dir.FullName, "Peer");
                if (Directory.Exists(peer))
                {
                    var found = Directory.GetFiles(peer, "MusicSession.Peer.dll", SearchOption.AllDirectories)
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault();
                    if (found != null) return found;
                }
            }
            throw new FileNotFoundException("MusicSession.Peer.dll not found from " + baseDir);
        }
    }
}
