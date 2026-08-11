// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later
//
// Print relocated qwords and any functions they reference.
// Usage: -postScript DumpPointers.java 113DEF8 1275210
//@category Prosperismo
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;

public class DumpPointers extends GhidraScript {
    @Override
    public void run() throws Exception {
        for (String text : getScriptArgs()) {
            long value = Long.parseUnsignedLong(text.replace("0x", ""), 16);
            Address base = currentProgram.getAddressFactory()
                .getDefaultAddressSpace().getAddress(value);
            println("\n===== " + text + " =====");
            for (int index = 0; index < 16; index++) {
                Address slot = base.add(index * 8L);
                long raw = getLong(slot);
                Address target = base.getAddressSpace().getAddress(raw);
                Function function = getFunctionContaining(target);
                println(slot + "  " + target +
                    (function == null ? "" : "  function=" + function.getEntryPoint()));
            }
        }
    }
}
