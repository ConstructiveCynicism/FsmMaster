using System.Runtime.CompilerServices;

// Core's types stay internal (as they were before the Core/loader split) rather than widening to
// public just to cross the assembly boundary - each loader project is the one and only consumer of
// this assembly, not a general-purpose library other mods reference. Add one line per loader project
// as later phases bring hk1221/hk1432/hk1578 online. Named by each loader's actual <AssemblyName>, not
// its project file name - FsmMaster.Silksong.csproj still builds an assembly literally called
// "FsmMaster" (see that csproj's own comment) to keep the shipped output unchanged.
[assembly: InternalsVisibleTo("FsmMaster")]
