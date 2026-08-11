// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later
//
// Decompile explicitly named virtual addresses and list their direct callers.
// Usage from analyzeHeadless: -postScript DumpFunctions.java E14F0 E5340
//@category Prosperismo
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;

public class DumpFunctions extends GhidraScript {
    @Override
    public void run() throws Exception {
        DecompInterface decompiler = new DecompInterface();
        decompiler.openProgram(currentProgram);
        decompiler.setSimplificationStyle("decompile");

        for (String text : getScriptArgs()) {
            long value = Long.parseUnsignedLong(text.replace("0x", ""), 16);
            Address address = currentProgram.getAddressFactory()
                .getDefaultAddressSpace().getAddress(value);
            Function function = getFunctionContaining(address);
            println("\n===== " + text + " -> " +
                (function == null ? "no function" : function.getEntryPoint()) + " =====");
            if (function == null) {
                continue;
            }

            println("callers:");
            for (Reference reference : getReferencesTo(function.getEntryPoint())) {
                Function caller = getFunctionContaining(reference.getFromAddress());
                if (caller != null) {
                    println("  " + caller.getEntryPoint() + " via " + reference.getFromAddress());
                }
            }

            DecompileResults result = decompiler.decompileFunction(function, 180, monitor);
            println(result.decompileCompleted()
                ? result.getDecompiledFunction().getC()
                : "decompile failed: " + result.getErrorMessage());
        }

        decompiler.dispose();
    }
}
