// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Prosperismo.HLE;

/// <summary>
/// Represents kernel result codes used by the Gen5 runtime. Prefixed with
/// ORBIS_GEN2 to distinguish from PS4-oriented ORBIS_* codes used by other
/// emulators such as shadPS4.
/// </summary>
/// <remarks>
/// <para>
/// The Prospero kernel encodes an error as <c>0x80020000 + errno</c>, using
/// FreeBSD 11 errno values. Extracted from decrypted 4.03 libkernel.sprx: 1395
/// sites perform the conversion literally as <c>lea r32,[r32-0x7ffe0000]</c>,
/// and sceKernelWaitEqueue's invalid-count path loads <c>0x80020016</c>
/// (EINVAL = 22) directly. A census of every <c>mov r32,0x8002xxxx</c> across
/// dominated by EINVAL at 1118 occurrences.
/// </para>
/// <para>
/// So a value here is not free to choose: the guest branches on it. Any code
/// added below must decode to the FreeBSD errno its name claims.
/// </para>
/// </remarks>
public enum OrbisGen2Result : int
{
    /// <summary>
    /// Indicates successful completion.
    /// </summary>
    ORBIS_GEN2_OK = 0,

    /// <summary>
    /// Indicates that the operation is not permitted for the calling thread.
    /// </summary>
    ORBIS_GEN2_ERROR_PERMISSION_DENIED = unchecked((int)0x80020001),

    /// <summary>
    /// Indicates that the requested export was not found.
    /// </summary>
    ORBIS_GEN2_ERROR_NOT_FOUND = unchecked((int)0x80020002),

    /// <summary>
    /// Indicates that one or more arguments were invalid. EINVAL (22).
    /// </summary>
    /// <remarks>
    /// Was 0x80020003, which decodes to ESRCH ("no such process"), not EINVAL.
    /// loads 0x80020016 on its nev &lt;= 0 path.
    /// </remarks>
    ORBIS_GEN2_ERROR_INVALID_ARGUMENT = unchecked((int)0x80020016),

    /// <summary>
    /// Indicates that an item already exists. EEXIST (17).
    /// </summary>
    /// <remarks>
    /// Was 0x80020004, which decodes to EINTR. That collision was the most
    /// dangerous code in this enum: EINTR is what retry loops key on, so a
    /// caller told "already exists" would retry the create forever.
    /// </remarks>
    ORBIS_GEN2_ERROR_ALREADY_EXISTS = unchecked((int)0x80020011),

    /// <summary>
    /// Indicates that completing the operation would deadlock.
    /// </summary>
    ORBIS_GEN2_ERROR_DEADLOCK = unchecked((int)0x8002000B),

    /// <summary>
    /// Indicates that the waited-on object was deleted while the caller was
    /// blocked on it. Matches the SCE kernel EACCES code that waiters of a
    /// deleted semaphore observe.
    /// </summary>
    ORBIS_GEN2_ERROR_DELETED = unchecked((int)0x8002000D),

    /// <summary>
    /// Indicates that the target resource is busy.
    /// </summary>
    ORBIS_GEN2_ERROR_BUSY = unchecked((int)0x80020010),

    /// <summary>
    /// Indicates that the operation should be retried later.
    /// </summary>
    ORBIS_GEN2_ERROR_TRY_AGAIN = unchecked((int)0x80020023),

    /// <summary>
    /// Indicates that a blocked wait was canceled (e.g. sceKernelCancelSema).
    /// Matches the SCE kernel ECANCELED code that canceled semaphore waiters
    /// observe.
    /// </summary>
    ORBIS_GEN2_ERROR_CANCELED = unchecked((int)0x80020055),

    /// <summary>
    /// Indicates that behavior is recognized but not implemented yet.
    /// </summary>
    /// <remarks>
    /// Deliberately OUT OF BAND: 0xFFFF is not a FreeBSD errno and this value
    /// code the console produces. That is the point — it marks a Prosperismo gap
    /// rather than impersonating ENOSYS (which would be 0x8002004E) and letting
    /// a guest handle it as a legitimate kernel refusal. Do not "fix" it to
    /// ENOSYS without deciding that silence is preferable to a loud gap.
    /// </remarks>
    ORBIS_GEN2_ERROR_NOT_IMPLEMENTED = unchecked((int)0x8002FFFF),

    /// <summary>
    /// Indicates that the operation timed out.
    /// </summary>
    ORBIS_GEN2_ERROR_TIMED_OUT = unchecked((int)0x8002003C),

    /// <summary>
    /// Indicates that memory access failed. EFAULT (14).
    /// </summary>
    /// <remarks>
    /// Was 0x80020101, which is not an errno at all and appears zero times in
    /// the second-most-used code in the tree, so a guest checking for EFAULT
    /// was never seeing it.
    /// </remarks>
    ORBIS_GEN2_ERROR_MEMORY_FAULT = unchecked((int)0x8002000E),

    /// <summary>
    /// Indicates that CPU execution trapped on an unsupported instruction.
    /// </summary>
    /// <remarks>
    /// Deliberately OUT OF BAND, like NOT_IMPLEMENTED: there is no kernel errno
    /// for "the emulator could not execute this instruction", and inventing one
    /// would hide an emulator defect behind a plausible guest-facing error.
    /// </remarks>
    ORBIS_GEN2_ERROR_CPU_TRAP = unchecked((int)0x80020102),
}
