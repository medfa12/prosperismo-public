// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later
//
// Print every code or data reference to explicitly named virtual addresses.
// Usage: -postScript DumpReferences.java B8520 14FBC0
//@category Prosperismo
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;

public class DumpReferences extends GhidraScript {
    @Override
    public void run() throws Exception {
        for (String text : getScriptArgs()) {
            long value = Long.parseUnsignedLong(text.replace("0x", ""), 16);
            Address address = currentProgram.getAddressFactory()
                .getDefaultAddressSpace().getAddress(value);
            println("\n===== " + text + " =====");
            for (Reference reference : getReferencesTo(address)) {
                Function function = getFunctionContaining(reference.getFromAddress());
                println("  from=" + reference.getFromAddress() +
                    " type=" + reference.getReferenceType() +
                    (function == null ? "" : " function=" + function.getEntryPoint()));
            }
        }
    }
}
