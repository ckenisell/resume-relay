# AppleTvControlLibrary (vendored)

Apple TV pairing and now-playing (MRP over AirPlay 2) for Resume Relay's Live Poster, from
[github.com/oznetmaster/AppleTvControlLibrary](https://github.com/oznetmaster/AppleTvControlLibrary)
(MIT, Copyright (c) 2026 Neil Colvin), at commit b18ffcf (version 2.2.7).

Included unchanged except that each project targets **net8.0** (the library itself targets net472 and net10.0;
its code builds and its 86 MRP tests pass on .NET 8). Only the three libraries Resume Relay uses are here:
`AppleTv.Hap` (pairing and encryption), `AppleTv.Mrp` (now playing) and `AppleTV.Companion.Discovery`
(finding Apple TVs on the network). It's written in C# 13, so building needs the .NET 10 SDK; the result runs
on .NET 8.

See LICENSE, ATTRIBUTIONS.md and THIRD-PARTY-NOTICES.txt. Apple, Apple TV, tvOS and AirPlay are trademarks of
Apple Inc.; this library and Resume Relay are not affiliated with or endorsed by Apple.
