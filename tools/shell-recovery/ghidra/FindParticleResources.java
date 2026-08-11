// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later
//
// Recovers the particle simulation constants from the PS5 shell eboot.
//
// Three earlier attempts with a scripted disassembler failed the same way: they
// located candidate sites by matching a struct offset (0x5e0, 0x1a0) and every
// match belonged to an unrelated class that happens to have a field there. In a
// 13 MB C++ binary those offsets collide constantly, and a linear disassembler
// has no type information to tell the objects apart.
//
// This uses Ghidra's decompiler instead, which tracks values across calls. The
// anchor is simulateParticles, identified independently by its own assert
// strings; from there the ResourcesCs pointer is followed backwards through
// actual data flow rather than by guessing at offsets.
//
//@category Prosperismo
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.pcode.*;
import ghidra.program.model.symbol.*;
import ghidra.program.model.scalar.Scalar;
import java.util.*;

public class FindParticleResources extends GhidraScript {

    // simulateParticles in 12.40, verified by its assert strings.
    private static final long SIMULATE = 0xE24F0L;

    private DecompInterface decomp;

    @Override
    public void run() throws Exception {
        decomp = new DecompInterface();
        decomp.openProgram(currentProgram);
        decomp.setSimplificationStyle("decompile");

        Address sim = addr(SIMULATE);
        println("=== callers of simulateParticles @ " + sim + " ===");

        Set<Function> callers = new LinkedHashSet<>();
        for (Reference r : getReferencesTo(sim)) {
            Function f = getFunctionContaining(r.getFromAddress());
            if (f != null) callers.add(f);
        }
        for (Function f : callers) println("  " + f.getName() + " @ " + f.getEntryPoint());

        // The systems are whatever is passed as argument 2. Walk each caller's
        // decompiled form and report where that argument originates.
        for (Function f : callers) {
            println("\n=== " + f.getName() + " ===");
            DecompileResults res = decomp.decompileFunction(f, 120, monitor);
            if (!res.decompileCompleted()) { println("  decompile failed"); continue; }
            HighFunction hf = res.getHighFunction();
            Iterator<PcodeOpAST> ops = hf.getPcodeOps();
            while (ops.hasNext()) {
                PcodeOpAST op = ops.next();
                if (op.getOpcode() != PcodeOp.CALL) continue;
                if (!op.getInput(0).getAddress().equals(sim)) continue;
                // input 0 is the target; 1.. are the arguments
                println("  call @ " + op.getSeqnum().getTarget()
                        + "  (" + (op.getNumInputs() - 1) + " args)");
                // Ghidra's inferred convention does not necessarily place the
                // system pointer where the SysV integer order would; inspect
                // every argument and follow the pointer-sized ones.
                for (int i = 1; i < op.getNumInputs(); i++) {
                    Varnode a = op.getInput(i);
                    println("    arg" + i + " size=" + a.getSize() + "  " + a);
                    if (a.getSize() == 8) trace(a, "      ", 0);
                }
            }
        }

        // Report the float constants reachable from whatever fills the block.
        println("\n=== done ===");
        decomp.dispose();
    }

    /** Walks a varnode's defining chain, printing where each value came from. */
    private void trace(Varnode vn, String indent, int depth) {
        if (vn == null || depth > 7) return;
        PcodeOp def = vn.getDef();
        if (def == null) {
            HighVariable hv = vn.getHigh();
            println(indent + "<- " + (hv != null ? hv.getName() : "input") + " " + vn);
            return;
        }
        println(indent + "<- " + def.getMnemonic() + " @ " + def.getSeqnum().getTarget());
        if (def.getOpcode() == PcodeOp.CALL) {
            Address t = def.getInput(0).getAddress();
            Function cf = getFunctionAt(t);
            println(indent + "   returns from " + (cf != null ? cf.getName() : t.toString()));
            if (cf != null) dumpFloats(cf, indent + "   ");
            return;
        }
        for (int i = 0; i < def.getNumInputs(); i++) trace(def.getInput(i), indent + "  ", depth + 1);
    }

    /** Prints every float literal a function loads from memory. */
    private void dumpFloats(Function f, String indent) {
        Listing listing = currentProgram.getListing();
        InstructionIterator it = listing.getInstructions(f.getBody(), true);
        int n = 0;
        while (it.hasNext() && n < 64) {
            Instruction ins = it.next();
            String m = ins.getMnemonicString();
            if (!m.startsWith("VMOVSS") && !m.startsWith("MOVSS")) continue;
            for (int i = 0; i < ins.getNumOperands(); i++) {
                Object[] objs = ins.getOpObjects(i);
                for (Object o : objs) {
                    if (!(o instanceof Address)) continue;
                    try {
                        float v = Float.intBitsToFloat(
                            currentProgram.getMemory().getInt((Address) o));
                        println(indent + String.format("%s  [%s] = %g", ins.getAddress(), o, v));
                        n++;
                    } catch (Exception ignored) { }
                }
            }
        }
    }

    private Address addr(long off) {
        return currentProgram.getAddressFactory().getDefaultAddressSpace().getAddress(off);
    }
}
