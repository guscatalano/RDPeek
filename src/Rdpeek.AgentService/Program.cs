using System.ServiceProcess;
using Rdpeek.AgentService;

// =====================================================================================
//  rdpeek-agent-service  —  an OPT-IN Windows Service that supervises the in-session
//  RDPeek agent (rdpeek-agent.exe serve) for locked-down / always-on hosts where admin
//  is available. This is an ALTERNATIVE to the scheduled-task path
//  (tools/install-agent-web.ps1); that path is unchanged and still works.
//
//  KEY CONSTRAINT: this process runs in **session 0**, which has no interactive RDP DVC.
//  It therefore never opens the channel itself — it launches rdpeek-agent.exe INTO each
//  active user session (CreateProcessAsUser) and supervises those processes. See
//  NativeMethods.cs / SessionLauncher.cs for the full rationale.
//
//  Usage:
//    rdpeek-agent-service                 run as a service (invoked by the SCM)
//    rdpeek-agent-service --agent <path>  override the agent exe path (else auto-resolved)
//    rdpeek-agent-service --console       run in the foreground for debugging (needs admin)
// =====================================================================================

var argList = args.ToList();

// --agent <path>: explicit agent exe override (highest precedence in AgentPathResolver).
int idx = argList.FindIndex(a => string.Equals(a, "--agent", StringComparison.OrdinalIgnoreCase));
if (idx >= 0 && idx + 1 < argList.Count)
    AgentPathResolver.ExplicitOverride = argList[idx + 1];

bool console = argList.Any(a => string.Equals(a, "--console", StringComparison.OrdinalIgnoreCase))
               || Environment.UserInteractive && !argList.Any(a => string.Equals(a, "--service", StringComparison.OrdinalIgnoreCase));

if (console)
{
    // Foreground mode: same supervision logic, logs to the console, no SCM.
    void Log(string m) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {m}");

    string serviceDir = AppContext.BaseDirectory;
    string? agentExe = AgentPathResolver.ResolveLive(serviceDir);
    if (agentExe is null)
    {
        Log($"ERROR: could not find {AgentPathResolver.AgentFileName}. Candidates: " +
            string.Join("; ", AgentPathResolver.EnumerateCandidates(
                AgentPathResolver.ExplicitOverride,
                Environment.GetEnvironmentVariable(AgentPathResolver.EnvVar),
                null, serviceDir)));
        return 1;
    }

    using var supervisor = new AgentSupervisor(agentExe, Log);
    supervisor.Start();

    Log("Running in --console mode. Press Ctrl+C to stop. " +
        "(Session-change events are NOT delivered in console mode; startup sessions only.)");

    var done = new ManualResetEventSlim(false);
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
    done.Wait();

    supervisor.Stop();
    return 0;
}

// Service mode: hand control to the SCM.
ServiceBase.Run(new AgentSupervisorService());
return 0;
