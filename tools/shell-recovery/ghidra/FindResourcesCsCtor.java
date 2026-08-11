// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later
//
// Finds the ResourcesCs constructor by structural signature rather than by
// offset matching, which failed three times: the offsets involved (0x1a0,
// 0x5e0) are common enough that every candidate found that way belonged to an
// unrelated class.
//
// ResourcesCs is 0xF8 bytes and its fields are floats written from .rodata, so
// its constructor is whatever function stores many distinct float literals into
// offsets below 0xF8 of a single base register. That shape is rare, and unlike
// an offset it cannot coincide with an unrelated object.
//
//@category Prosperismo
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.mem.MemoryAccessException;
import java.util.*;

public class FindResourcesCsCtor extends GhidraScript {

    private static final int STRUCT_LIMIT = 0x120;   // a little past 0xF8
    private static final int MIN_FLOATS = 6;

    @Override
    public void run() throws Exception {
        Listing listing = currentProgram.getListing();
        FunctionIterator funcs = listing.getFunctions(true);
        List<Object[]> hits = new ArrayList<>();

        while (funcs.hasNext() && !monitor.isCancelled()) {
            Function f = funcs.next();
            // literal float loads seen in this function, by value
            Set<Float> consts = new LinkedHashSet<>();
            int storesInRange = 0;

            InstructionIterator it = listing.getInstructions(f.getBody(), true);
            while (it.hasNext()) {
                Instruction ins = it.next();
                String m = ins.getMnemonicString();

                if (m.startsWith("VMOVSS") || m.startsWith("MOVSS")) {
                    // a float literal loaded from memory
                    for (int i = 0; i < ins.getNumOperands(); i++) {
                        for (Object o : ins.getOpObjects(i)) {
                            if (!(o instanceof Address)) continue;
                            try {
                                float v = Float.intBitsToFloat(
                                    currentProgram.getMemory().getInt((Address) o));
                                if (!Float.isNaN(v) && !Float.isInfinite(v)
                                        && v != 0.0f && Math.abs(v) < 1e6f) {
                                    consts.add(v);
                                }
                            } catch (MemoryAccessException ignored) { }
                        }
                    }
                    // a store into a small positive struct offset
                    String s = ins.toString();
                    int b = s.indexOf("+ 0x");
                    if (b >= 0 && s.indexOf("[") >= 0 && s.indexOf("]") > s.indexOf("[")) {
                        try {
                            String hex = s.substring(b + 4).split("[^0-9a-fA-F]")[0];
                            int off = Integer.parseInt(hex, 16);
                            if (off > 0 && off < STRUCT_LIMIT) storesInRange++;
                        } catch (Exception ignored) { }
                    }
                }
            }

            if (consts.size() >= MIN_FLOATS && storesInRange >= MIN_FLOATS) {
                hits.add(new Object[]{f, consts.size(), storesInRange, consts});
            }
        }

        hits.sort((a, b) -> ((Integer) b[1]) - ((Integer) a[1]));
        println("=== candidate ResourcesCs constructors: " + hits.size() + " ===");
        int shown = 0;
        for (Object[] h : hits) {
            Function f = (Function) h[0];
            @SuppressWarnings("unchecked") Set<Float> cs = (Set<Float>) h[3];
            println(String.format("%s @ %s   %d distinct floats, %d in-range stores",
                    f.getName(), f.getEntryPoint(), (Integer) h[1], (Integer) h[2]));
            StringBuilder sb = new StringBuilder("    ");
            int n = 0;
            for (Float v : cs) {
                sb.append(String.format("%g  ", v));
                if (++n % 8 == 0) { println(sb.toString()); sb = new StringBuilder("    "); }
            }
            if (sb.length() > 4) println(sb.toString());
            if (++shown >= 12) break;
        }
    }
}
