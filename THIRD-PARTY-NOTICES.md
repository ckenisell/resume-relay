# Third-party notices

Resume Relay includes or uses the following third-party software, each under its own license.

| Component | Used for | License |
|---|---|---|
| [AppleTvControlLibrary](https://github.com/oznetmaster/AppleTvControlLibrary) by Neil Colvin | Apple TV support | MIT (below) |
| [pyatv](https://github.com/postlund/pyatv) by Pierre Ståhl | Apple TV protocol definitions, via AppleTvControlLibrary | MIT (below) |
| [.NET](https://github.com/dotnet/runtime) | the runtime | MIT |
| [BouncyCastle.Cryptography](https://www.bouncycastle.org/csharp/) | Apple TV pairing encryption | MIT |
| [Google.Protobuf](https://github.com/protocolbuffers/protobuf) | Apple TV messages | BSD 3-Clause |
| [plist-cil](https://github.com/claunia/plist-cil) | Apple TV details | MIT |
| [Microsoft Edge WebView2](https://developer.microsoft.com/microsoft-edge/webview2/) | the Windows app window | Microsoft Software License |
| [FFmpeg](https://ffmpeg.org) (Docker and Linux, a separate program) | making Live Poster videos | LGPL / GPL |
| [Intel Media Driver](https://github.com/intel/media-driver) (Docker image only, a separate program) | Live Poster videos on Intel video hardware (Quick Sync) | MIT, with Intel's redistributable GPU kernels |
| [Inter](https://github.com/rsms/inter) by The Inter Project Authors | the words on Live Poster's trivia pictures | SIL Open Font License 1.1 (below) |

Poster information for streaming apps comes from TMDB. This product uses TMDB and the TMDB APIs but is not
endorsed, certified, or otherwise approved by TMDB.

TV listings and episode names come from [TVmaze](https://www.tvmaze.com), licensed under
[CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/).

Trivia questions from the web come from [Open Trivia DB](https://opentdb.com), licensed under
[CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/).

Apple TV is a trademark of Apple Inc. Resume Relay isn't affiliated with or endorsed by Apple, Plex, Jellyfin, Zidoo
or Kaleidescape.

## Inter

```
Inter 4.0, Copyright 2016-2023 The Inter Project Authors (https://github.com/rsms/inter)

This Font Software is licensed under the SIL Open Font License, Version 1.1.

PREAMBLE
The goals of the Open Font License (OFL) are to stimulate worldwide
development of collaborative font projects, to support the font
creation efforts of academic and linguistic communities, and to
provide a free and open framework in which fonts may be shared and
improved in partnership with others.

The OFL allows the licensed fonts to be used, studied, modified and
redistributed freely as long as they are not sold by themselves. The
fonts, including any derivative works, can be bundled, embedded,
redistributed and/or sold with any software provided that any
reserved names are not used by derivative works. The fonts and
derivatives, however, cannot be released under any other type of
license. The requirement for fonts to remain under this license
does not apply to any document created using the fonts or their
derivatives.

DEFINITIONS
Font Software refers to the set of files released by the
Copyright Holder(s) under this license and clearly marked as such.
This may include source files, build scripts and documentation.

Reserved Font Name refers to any names specified as such after
the copyright statement(s).

Original Version refers to the collection of Font Software
components as distributed by the Copyright Holder(s).

Modified Version refers to any derivative made by adding to,
deleting, or substituting in part or in whole any of the components
of the Original Version, by changing formats or by porting the Font
Software to a new environment.

Author refers to any designer, engineer, programmer, technical
writer or other person who contributed to the Font Software.

PERMISSION & CONDITIONS
Permission is hereby granted, free of charge, to any person
obtaining a copy of the Font Software, to use, study, copy, merge,
embed, modify, redistribute, and sell modified and unmodified copies
of the Font Software, subject to the following conditions:

1) Neither the Font Software nor any of its individual components,
   in Original or Modified Versions, may be sold by itself.

2) Original or Modified Versions of the Font Software may be bundled,
   redistributed and/or sold with any software, provided that each
   copy contains the above copyright notice and this license. These
   can be included either as stand-alone text files, human-readable
   headers or in the appropriate machine-readable metadata fields
   within text or binary files as long as those fields can be easily
   viewed by the user.

3) No Modified Version of the Font Software may use the Reserved Font
   Name(s) unless explicit written permission is granted by the
   corresponding Copyright Holder. This restriction only applies to
   the primary font name as presented to the users.

4) The name(s) of the Copyright Holder(s) or the Author(s) of the
   Font Software shall not be used to promote, endorse or advertise
   any Modified Version, except to acknowledge the contribution(s)
   of the Copyright Holder(s) and the Author(s) or with their
   explicit written permission.

5) The Font Software, modified or unmodified, in part or in whole,
   must be distributed entirely under this license, and must not be
   distributed under any other license. The requirement for fonts to
   remain under this license does not apply to any document created
   using the Font Software.

TERMINATION
This license becomes null and void if any of the above conditions
are not met.

DISCLAIMER
THE FONT SOFTWARE IS PROVIDED AS IS, WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO ANY WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT
OF COPYRIGHT, PATENT, TRADEMARK, OR OTHER RIGHT. IN NO EVENT SHALL
THE COPYRIGHT HOLDER BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, INCLUDING ANY GENERAL, SPECIAL, INDIRECT, INCIDENTAL, OR
CONSEQUENTIAL DAMAGES, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF THE USE OR INABILITY TO USE THE FONT
SOFTWARE OR FROM OTHER DEALINGS IN THE FONT SOFTWARE.
```

## AppleTvControlLibrary

```
MIT License

Copyright (c) 2026 Neil Colvin

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## AppleTvControlLibrary's own third-party notices (including pyatv)

```
THIRD-PARTY NOTICES
===================

This file contains license notices for third-party projects that were consulted as a protocol
reference during the development of this library, and/or whose packages are referenced as NuGet
dependencies. No source code from the projects below is compiled into or distributed with this
library except where explicitly noted as a NuGet dependency, or as a vendored file explicitly
called out below. See ATTRIBUTIONS.md for details on how each project was used.

-------------------------------------------------------------------------------
pyatv 0.18.0
https://github.com/postlund/pyatv
-------------------------------------------------------------------------------

Vendored files: the .proto message definitions under
src/AppleTv.Mrp/Protobuf/pyatv/protocols/mrp/protobuf/ are copied, with only a
`csharp_namespace` option added for C# codegen, from pyatv 0.18.0's
pyatv/protocols/mrp/protobuf/ directory. These files describe Apple's MRP wire format
(message/field names and numeric tags) and are distributed here so that protobuf codegen
produces byte-exact, citable message types instead of hand-authored approximations. This
copyright notice is retained per the MIT license below.

MIT License

Copyright (c) 2017 Pierre Ståhl

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.

-------------------------------------------------------------------------------
srptools 1.0.1
https://github.com/idlesign/srptools
-------------------------------------------------------------------------------

BSD 3-Clause License

Copyright (c) 2017, Igor Starikov
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its
   contributors may be used to endorse or promote products derived from
   this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE
LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
POSSIBILITY OF SUCH DAMAGE.

-------------------------------------------------------------------------------
BouncyCastle.Cryptography (NuGet dependency)
https://www.bouncycastle.org/csharp/
-------------------------------------------------------------------------------

Copyright (c) 2000-2024 The Legion of the Bouncy Castle Inc.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
```
