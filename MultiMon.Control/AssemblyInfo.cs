using System.Runtime.CompilerServices;
using System.Windows;

// The stress harness drives the real PerformanceController and needs its debug-layer / wait hooks.
[assembly: InternalsVisibleTo("MultiMon.Stress")]

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]
