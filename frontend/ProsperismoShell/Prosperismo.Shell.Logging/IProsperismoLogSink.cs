// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Prosperismo.Logging;

public interface IProsperismoLogSink
{
    void Write(in LogEntry entry);
}
