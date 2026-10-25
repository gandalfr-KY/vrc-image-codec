# Third-party notices

The AV1 encoder, probability/quantization tables and integer transforms derive
from [tinyavif](https://github.com/nekotrix/tinyavif), commit
`b5d32a0cafd24b984dd289566c4d5d9b1560a541`.

Copyright (c) 2024–2025, the tinyavif contributors. BSD-2-Clause.
The upstream BSD license and AOMedia Patent License 1.0 are reproduced in
`ThirdParty/AV1/` and must accompany redistribution of
the port and shaders. The port adds Udon scheduling, GPU transformation and
readback, context packing, a bounded AVIF wrapper and camera integration.
It is a restricted intra encoder, not SVT-AV1 or a hardware AV1 encoder.
The upstream project describes itself as a proof of concept; this release makes
no claim to match production encoders in compression efficiency or AV1 coverage.

The range decoder (`aom_dsp/entdec.c`), intra predictors, chroma-from-luma arithmetic, probability adaptation,
ADST/DCT transforms, CDEF, PSNR coefficient trellis decisions and
default-CDF-derived rate-cost tables also derive from the AOM AV1 reference
implementation (https://aomedia.googlesource.com/aom/), pinned at libaom 3.14.1,
commit `03087864cf4bea6abb0d28f95cf7843511413d8f`.
Copyright (c) 2016, Alliance for Open Media. All rights reserved.
Range decoder: Copyright (c) 2001–2016, Alliance for Open Media. All rights reserved.
These portions use the BSD-2-Clause license and AOMedia Patent License 1.0.
The full BSD notice is included in `LICENSE`.

The AOMedia patent license is also included as `PATENTS` at the package root. `LICENSE` carries both the project
and upstream BSD notices, so copying only that folder preserves its notices.

