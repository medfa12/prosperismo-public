// Copyright (C) 2026 Prosperismo Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

#ifndef PROSPERISMO_CLI_OPTIONS_H_
#define PROSPERISMO_CLI_OPTIONS_H_

#include "emulator.h"

#include <string>

namespace Emulator::Cli {

bool Parse(int argc, char* argv[], RunOptions& options, bool& show_help, std::string& error);

} // namespace Emulator::Cli

#endif // PROSPERISMO_CLI_OPTIONS_H_
