// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

// The shell reads exactly one symbol from the donor's VideoOut assembly
// (21,926 lines): this enum. Shimmed rather than importing that stack.
namespace Prosperismo.Libs.VideoOut;

public enum VideoOutRefreshRate
{
    Unknown = 0,
    Hz59_94 = 1,
    Hz50 = 2,
    Hz60 = 3,
    Hz30 = 4,
    Hz24 = 5,
}
