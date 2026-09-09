using System.Runtime.CompilerServices;

// The stress harness drives the real PerformanceController and needs its wait / resource-sample hooks.
[assembly: InternalsVisibleTo("MultiMon.Stress.Mac")]
