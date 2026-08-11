// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later
//
// Decompiles exact firmware virtual addresses without requiring a whole-eboot
// auto-analysis pass first. Useful for NPXS40087, where switch/non-return
// analysis of the full 21 MB executable can take many minutes.
// Usage: -noanalysis -postScript DecompileExactFunctions.java EA290 C24700
//@category Prosperismo
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;

public class DecompileExactFunctions extends GhidraScript {
    @Override
    public void run() throws Exception {
        DecompInterface decompiler = new DecompInterface();
        decompiler.openProgram(currentProgram);
        decompiler.setSimplificationStyle("decompile");

        for (String text : getScriptArgs()) {
            long value = Long.parseUnsignedLong(text.replace("0x", ""), 16);
            Address address = currentProgram.getAddressFactory()
                .getDefaultAddressSpace().getAddress(value);
            disassemble(address);
            Function function = getFunctionContaining(address);
            if (function == null) {
                function = createFunction(address, null);
            }

            println("\n===== " + text + " -> " +
                (function == null ? "no function" : function.getEntryPoint()) + " =====");
            if (function == null) {
                continue;
            }

            DecompileResults result = decompiler.decompileFunction(function, 180, monitor);
            println(result.decompileCompleted()
                ? result.getDecompiledFunction().getC()
                : "decompile failed: " + result.getErrorMessage());
        }

        decompiler.dispose();
    }
}
