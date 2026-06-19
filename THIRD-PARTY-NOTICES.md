# Third-Party Notices

MultiMon 1.0.0 includes or references the following third-party components. Test-only dependencies
(xUnit, coverlet) are not distributed and are not listed here.

---

## Vortice.Direct3D11, Vortice.DXGI, Vortice.D3DCompiler, Vortice.MediaFoundation

**Version:** 3.8.3
**Author:** Amer Koleci and contributors
**Project:** https://github.com/amerkoleci/Vortice.Windows
**Licence:** MIT

> MIT License
>
> Copyright (c) Amer Koleci and contributors
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
> associated documentation files (the "Software"), to deal in the Software without restriction,
> including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
> and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
> subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial
> portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT
> LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
> IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
> LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
> WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

---

## Microsoft.Windows.CsWin32

**Version:** 0.3.287
**Author:** Microsoft
**Project:** https://github.com/microsoft/CsWin32
**Licence:** MIT

> MIT License
>
> Copyright (c) Microsoft Corporation
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
> associated documentation files (the "Software"), to deal in the Software without restriction,
> including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
> and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
> subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial
> portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT
> LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
> IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
> LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
> WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

---

## Microsoft.Extensions.DependencyInjection

**Version:** 10.0.9
**Author:** Microsoft
**Project:** https://github.com/dotnet/runtime
**Licence:** MIT

> The MIT License (MIT)
>
> Copyright (c) .NET Foundation and Contributors
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
> associated documentation files (the "Software"), to deal in the Software without restriction,
> including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
> and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
> subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial
> portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT
> LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
> IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
> LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
> WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

---

## System.Management

**Version:** 10.0.9
**Author:** Microsoft
**Project:** https://github.com/dotnet/runtime
**Licence:** MIT

Same MIT licence text as Microsoft.Extensions.DependencyInjection above (same copyright holder and
project).

---

## Snappy block decompressor (vendored, from scratch)

**Location:** `MultiMon.Decode/Hap/Snappy/SnappyDecoder.cs`
**Status:** Written from scratch by the MultiMon authors implementing the Snappy compressed-data
format specification. This is a decompress-only, minimal implementation of the Snappy block format
(varint length preamble + literal/copy elements) as documented in the Snappy format description
at https://github.com/google/snappy/blob/main/format_description.txt.

MultiMon's Snappy decoder shares no code with the Google Snappy C++ library or any Snappy wrapper.
No licence from Google's Snappy project is required because the implementation is independent. The
Snappy format specification itself is unencumbered.

---

## HapQ YCoCg-to-RGB conversion (from-scratch shader, HAP spec reference)

**Location:** `MultiMon.Graphics/Shaders/Quad.hlsl` (`PSSampleYCoCg` function)
**Status:** Written from scratch by the MultiMon authors. The scaled YCoCg-to-RGB conversion
constants and channel layout (`Co` in R, `Cg` in G, scale in B, luma `Y` in A for BC3/HapQ) are
derived from the publicly documented HAP codec specification (`ScaledCoCgYToRGBA`) and from the
open-source HAP codec project at https://github.com/Vidvox/hap.

The HAP codec specification and the open-source HAP project are made available under the BSD
2-clause licence by Vidvox LLC. MultiMon's shader is an independent reimplementation referencing
the public specification; it does not incorporate source code from the HAP project. Full licence
text for the HAP project:

> Copyright (c) 2012-2024, Vidvox LLC
> All rights reserved.
>
> Redistribution and use in source and binary forms, with or without modification, are permitted
> provided that the following conditions are met:
>
> 1. Redistributions of source code must retain the above copyright notice, this list of conditions
>    and the following disclaimer.
>
> 2. Redistributions in binary form must reproduce the above copyright notice, this list of
>    conditions and the following disclaimer in the documentation and/or other materials provided
>    with the distribution.
>
> THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR
> IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND
> FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR
> CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
> DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
> DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY,
> WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY
> WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
