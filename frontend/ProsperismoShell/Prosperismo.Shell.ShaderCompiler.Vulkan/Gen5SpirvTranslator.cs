// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using Prosperismo.ShaderCompiler;

namespace Prosperismo.ShaderCompiler.Vulkan;

/// <summary>
/// Host Vulkan features the extra vertex position exports (EXP POS1..POS3)
/// need before the translator is allowed to emit the matching SPIR-V builtin.
/// Every flag defaults to <see langword="false"/> because
/// <c>VulkanVideoPresenter.CreateDevice</c> enables none of them today
/// (src/Prosperismo.Libs/VideoOut/VulkanVideoPresenter.cs:4153 builds
/// <c>PhysicalDeviceFeatures</c> without ShaderClipDistance/ShaderCullDistance/
/// LargePoints, and never queries <c>PhysicalDeviceVulkan12Features</c> for
/// shaderOutputLayer/shaderOutputViewportIndex). Emitting a capability the
/// device did not enable is invalid Vulkan usage, so the conservative default
/// makes the translator refuse loudly instead of producing a module the driver
/// may reject or mis-execute.
/// </summary>
public readonly record struct Gen5HostVertexOutputFeatures(
    bool ShaderClipDistance = false,
    bool ShaderCullDistance = false,
    bool LargePoints = false,
    bool ShaderOutputLayer = false,
    bool ShaderOutputViewportIndex = false);

public static partial class Gen5SpirvTranslator
{
    /// <summary>
    /// Where translator diagnostics go. Defaults to stderr, matching the
    /// <c>[LOADER][WARN]</c> lines the CPU-side evaluator fallbacks print, so a
    /// running emulator always sees a degraded translation. Tests replace it.
    /// </summary>
    public static Action<string>? DiagnosticSink { get; set; }

    private static readonly Lock _diagnosticLock = new();
    private static readonly HashSet<string> _reportedDiagnostics = [];

    /// <summary>
    /// Emits <paramref name="message"/> at most once per <paramref name="key"/>
    /// for the lifetime of the process. Translation runs inside the per-draw
    /// pipeline build, so an undeduplicated message would repeat every frame.
    /// </summary>
    internal static void ReportDiagnosticOnce(string key, string message)
    {
        lock (_diagnosticLock)
        {
            if (!_reportedDiagnostics.Add(key))
            {
                return;
            }
        }

        var sink = DiagnosticSink;
        if (sink is not null)
        {
            sink(message);
            return;
        }

        Console.Error.WriteLine(message);
    }

    /// <summary>
    /// Forgets which diagnostics have already been reported. Tests call this so
    /// the deduplication of a previous test does not silence the next one.
    /// </summary>
    public static void ResetDiagnosticDeduplication()
    {
        lock (_diagnosticLock)
        {
            _reportedDiagnostics.Clear();
        }
    }

    private const uint ScalarRegisterCount = 256;
    private const uint VectorRegisterCount = 512;

    /// <summary>
    /// EXP target 20 - the RDNA NGG primitive (connectivity) export.
    /// </summary>
    /// <remarks>
    /// immediately after its <c>s_sendmsg GS_ALLOC_REQ</c>; the encoding seen
    /// in <c>AgcCompositor.elf</c> is the word 0xF8000941, whose EXP target
    /// field ((0x941 &gt;&gt; 4) &amp; 0x3F) is 20.
    /// </remarks>
    internal const uint NggPrimitiveExportTarget = 20;
    // EXTRACTED: ===== 02.pdf p1 ===== gives each workgroup up to 64 KiB of LDS.
    private const uint LdsDwordCount = 64 * 1024 / sizeof(uint);
    // EXTRACTED: ===== 62.pdf p1 ===== defines s_barrier as local workgroup
    // synchronization. DIFFERENTIAL: the Metal backend covers threadgroup and
    // device memory. SPIR-V 0x948 adds Uniform and Image memory to Workgroup.
    private const uint WorkgroupBarrierMemorySemantics = 0x948;
    // Graphics stages model LDS as a per-invocation Private array rather than
    // real workgroup-shared memory. A full 32 KB Private array per vertex/pixel
    // invocation is wasteful and risks Metal compile limits, and per-invocation
    // write-then-read correctness only needs deterministic address→slot masking,
    // so a smaller array is safe.
    private const uint PrivateLdsDwordCount = 2048;
    private const uint RdnaWaveLaneCount = 32;

    public static bool TryCompilePixelShader(
        Gen5ShaderState state,
        Gen5ShaderEvaluation evaluation,
        Gen5PixelOutputKind outputKind,
        out Gen5SpirvShader shader,
        out string error,
        int globalBufferBase = 0,
        int totalGlobalBufferCount = -1,
        int imageBindingBase = 0,
        int initialScalarBufferIndex = -1,
        int pixelRenderTargetSlot = 0,
        uint pixelInputEnable = 0,
        uint pixelInputAddress = 0,
        ulong storageBufferOffsetAlignment = 1,
        uint waveLaneCount = RdnaWaveLaneCount) =>
        TryCompilePixelShader(
            state,
            evaluation,
            [new Gen5PixelOutputBinding((uint)pixelRenderTargetSlot, 0, outputKind)],
            out shader,
            out error,
            globalBufferBase,
            totalGlobalBufferCount,
            imageBindingBase,
            initialScalarBufferIndex,
            pixelInputEnable,
            pixelInputAddress,
            storageBufferOffsetAlignment,
            waveLaneCount: waveLaneCount);

    public static bool TryCompilePixelShader(
        Gen5ShaderState state,
        Gen5ShaderEvaluation evaluation,
        IReadOnlyList<Gen5PixelOutputBinding> outputs,
        out Gen5SpirvShader shader,
        out string error,
        int globalBufferBase = 0,
        int totalGlobalBufferCount = -1,
        int imageBindingBase = 0,
        int initialScalarBufferIndex = -1,
        uint pixelInputEnable = 0,
        uint pixelInputAddress = 0,
        ulong storageBufferOffsetAlignment = 1,
        uint hostSubgroupSize = RdnaWaveLaneCount,
        uint waveLaneCount = RdnaWaveLaneCount)
    {
        if (outputs.Count > 8 || outputs.Any(output => output.GuestSlot > 7))
        {
            shader = default!;
            error = "pixel outputs must contain at most eight guest slots in the 0..7 range";
            return false;
        }

        if (outputs.Select(output => output.GuestSlot).Distinct().Count() != outputs.Count ||
            outputs.Select(output => output.HostLocation).Distinct().Count() != outputs.Count)
        {
            shader = default!;
            error = "pixel output guest slots and host locations must be unique";
            return false;
        }

        if (!outputs
                .OrderBy(output => output.HostLocation)
                .Select((output, index) => output.HostLocation == (uint)index)
                .All(isDense => isDense))
        {
            shader = default!;
            error = "pixel output host locations must be dense in the 0..N-1 range";
            return false;
        }

        var context = new CompilationContext(
            Gen5SpirvStage.Pixel,
            state,
            evaluation,
            outputs,
            1,
            1,
            1,
            globalBufferBase,
            totalGlobalBufferCount,
            imageBindingBase,
            initialScalarBufferIndex,
            pixelInputEnable: pixelInputEnable,
            pixelInputAddress: pixelInputAddress,
            waveLaneCount: waveLaneCount,
            storageBufferOffsetAlignment: storageBufferOffsetAlignment,
            hostSubgroupSize: hostSubgroupSize);
        return context.TryCompile(out shader, out error);
    }

    public static bool TryCompileVertexShader(
        Gen5ShaderState state,
        Gen5ShaderEvaluation evaluation,
        out Gen5SpirvShader shader,
        out string error,
        int globalBufferBase = 0,
        int totalGlobalBufferCount = -1,
        int imageBindingBase = 0,
        int initialScalarBufferIndex = -1,
        int requiredVertexOutputCount = 0,
        ulong storageBufferOffsetAlignment = 1,
        uint? vertexPositionOutputControl = null,
        Gen5HostVertexOutputFeatures hostVertexOutputFeatures = default,
        uint hostSubgroupSize = RdnaWaveLaneCount,
        uint waveLaneCount = RdnaWaveLaneCount,
        Gen5TessellationDomainSeeding? domainSeeding = null,
        Gen5NggPrimitiveConnectivity? nggPrimitiveConnectivity = null)
    {
        var context = new CompilationContext(
            Gen5SpirvStage.Vertex,
            state,
            evaluation,
            [],
            1,
            1,
            1,
            globalBufferBase,
            totalGlobalBufferCount,
            imageBindingBase,
            initialScalarBufferIndex,
            requiredVertexOutputCount: requiredVertexOutputCount,
            waveLaneCount: waveLaneCount,
            storageBufferOffsetAlignment: storageBufferOffsetAlignment,
            vertexPositionOutputControl: vertexPositionOutputControl,
            hostVertexOutputFeatures: hostVertexOutputFeatures,
            hostSubgroupSize: hostSubgroupSize,
            domainSeeding: domainSeeding,
            nggPrimitiveConnectivity: nggPrimitiveConnectivity);
        return context.TryCompile(out shader, out error);
    }

    /// <param name="ldsDwordCount">Optional cap on the declared workgroup
    /// array. The PS5 gives a workgroup 64 KB of LDS and that is the default,
    /// but a host may allow less — Apple GPUs cap threadgroup memory at 32 KB —
    /// and pipeline creation fails outright when the declaration exceeds the
    /// device limit. Addresses are range-checked either way, and an access past
    /// the allocation is recorded by the ldsAddressOutOfRange module flag, so a
    /// shader that genuinely needs more is reported rather than silently wrong.
    /// </param>
    public static bool TryCompileComputeShader(
        Gen5ShaderState state,
        Gen5ShaderEvaluation evaluation,
        uint localSizeX,
        uint localSizeY,
        uint localSizeZ,
        out Gen5SpirvShader shader,
        out string error,
        int totalGlobalBufferCount = -1,
        int initialScalarBufferIndex = -1,
        uint waveLaneCount = 32,
        ulong storageBufferOffsetAlignment = 1,
        uint hostSubgroupSize = RdnaWaveLaneCount,
        uint ldsDwordCount = LdsDwordCount,
        Gen5MergedWaveVgprSeeding? mergedWaveSeeding = null)
    {
        var context = new CompilationContext(
            Gen5SpirvStage.Compute,
            state,
            evaluation,
            [],
            Math.Max(localSizeX, 1),
            Math.Max(localSizeY, 1),
            Math.Max(localSizeZ, 1),
            0,
            totalGlobalBufferCount,
            0,
            initialScalarBufferIndex,
            waveLaneCount: waveLaneCount,
            storageBufferOffsetAlignment: storageBufferOffsetAlignment,
            hostSubgroupSize: hostSubgroupSize,
            ldsDwordLimit: ldsDwordCount,
            mergedWaveSeeding: mergedWaveSeeding);
        return context.TryCompile(out shader, out error);
    }

    internal static SpirvImageFormat DecodeStorageImageFormat(
        uint dataFormat,
        uint numberType) =>
        CompilationContext.DecodeStorageImageFormat(dataFormat, numberType);

    private sealed partial class CompilationContext
    {
        private static readonly bool _forcePackedStoreExecValues =
            Environment.GetEnvironmentVariable(
                "PROSPERISMO_FORCE_PACKED_STORE_EXEC_VALUES") == "1";
        private const uint ImageDescriptorDwords = 8;
        private const uint SamplerDescriptorDwords = 4;
        private const int ScalarRegisterCount = 128;
        private const long InitialScalarDefinition = -1;
        private const long ConflictingScalarDefinition = -2;
        private const long UnreachableScalarDefinition = -3;

        private readonly SpirvModuleBuilder _module = new();
        private readonly Gen5SpirvStage _stage;
        private readonly Gen5ShaderState _state;
        private readonly Gen5ShaderEvaluation _evaluation;
        private readonly IReadOnlyList<Gen5PixelOutputBinding> _pixelOutputBindings;
        // Guest wave width (32 or 64).
        //
        // COMPUTE. EXTRACTED: taken from COMPUTE_DISPATCH_INITIATOR.CS_W32_EN,
        // decoded at AgcExports.cs:10037 as
        // `(initiator & (1u << 15)) != 0 ? 32u : 64u`. Note the polarity: a
        // dispatch that does NOT set the bit is wave64, so wave64 is the
        // DEFAULT for compute, not an exotic case.
        //
        // GRAPHICS. ASSUMED 32, and now assumed EXPLICITLY - see
        // ValidateWaveWidthIsModelled. Nothing in this emulator decodes a
        // graphics wave width today: the AGC shader object's own registers do
        // not carry one: SPI_SHADER_PGM_RSRC1_GS has no wave-size field, and
        // upper half (value & 0xFFF00000 == 0x62200000), which under the
        // published GFX10.3 layout is DX10_CLAMP, MEM_ORDERED and
        // GS_VGPR_COMP_CNT=3 with WGP_MODE clear - no bit left over. The scan
        // producing that count is
        // Gen5WaveWidthTests.NoShippedShaderObjectCarriesAGraphicsWaveSizeField.
        // The register that would
        // carry it on GFX10.3 - VGT_SHADER_STAGES_EN - has a Prospero field
        // layout this project has NOT confirmed. See
        // NggPrimitiveShader.Ps5NggVertexStageConfiguration: the shipped word
        // is 0x02002000, whose only set bits are 13 and 25, which matches no
        // GFX10.3 field assignment this project can cite. So the graphics wave
        // width cannot be read off ground truth from here, and pretending to
        // decode it would be worse than saying so.
        private readonly uint _waveLaneCount;
        private readonly bool _emulateWave64;

        // Host subgroup width the emitted module assumes, i.e. the value the
        // device reports in VkPhysicalDeviceSubgroupProperties::subgroupSize.
        // An RDNA guest wave32 maps one-to-one onto a host subgroup only when
        // this is exactly 32, which is why 32 is the documented default: it is
        // correct on NVIDIA, and it is what every caller has silently assumed
        // since the translator was written. It is NOT correct on Intel (SIMD
        // 8/16/32) or on an AMD device running wave64 subgroups, and the
        // translator now says so instead of masking lane ids with a magic 31.
        private readonly uint _hostSubgroupSize;

        // Safety valve for the PC-dispatcher loop. Each iteration executes one
        // GCN basic block; a correctly-translated shader always reaches its
        // terminal block (pc out of range -> default -> exit) well within any
        // real control flow. A mistranslated shader whose loop-exit condition is
        // wrong would otherwise spin the dispatcher forever, hanging the single
        // Metal queue and freezing every later submission (black screen, no
        // recovery). Bounding the iteration count guarantees the invocation
        // terminates instead: the effect may be wrong for that shader, but the
        // GPU never wedges. 0 disables the guard (original unbounded behaviour).
        private static readonly int _maxDispatcherSteps =
            int.TryParse(
                Environment.GetEnvironmentVariable("PROSPERISMO_SHADER_MAX_STEPS"),
                out var maxSteps) && maxSteps >= 0
                ? maxSteps
                : 100_000;

        // Opt IN to refusing a shader whose EXP POS1..POS3 exports cannot be translated
        // faithfully. Default is to announce the drop once per shader and keep rendering.
        //
        // Refusing is the more principled position -- silently dropping clip/cull distances,
        // point size, gl_Layer and gl_ViewportIndex is exactly the silent-wrong class this work
        // set out to eliminate. But refusing today costs more than it buys: nothing supplies
        // PA_CL_VS_OUT_CNTL to the translator yet, and CreateDevice enables none of the required
        // device features, so the refusal fires for EVERY vertex shader that exports POS1..POS3
        // and turns "renders without clip planes" into "renders nothing at all". The loud
        // diagnostic already delivers the visibility; the draw kill only adds a visual
        // regression on top of it.
        //
        // Flip this to the strict default once PA_CL_VS_OUT_CNTL is plumbed through
        // AgcExports' TryCompileVertexShader call sites and CreateDevice enables
        // shaderClipDistance/shaderCullDistance/shaderOutputLayer/shaderOutputViewportIndex.
        private static readonly bool _refuseDroppedPositionExports =
            Environment.GetEnvironmentVariable(
                "PROSPERISMO_SPIRV_STRICT_POSITION_EXPORTS") == "1";

        // Diagnostic coverage probe. When enabled, every selected MRT export
        // writes opaque magenta while preserving the shader's control flow,
        // EXEC mask, geometry and raster state. This separates missing
        // rasterization from valid fragments whose translated values are zero.
        private static readonly bool _forcePixelMagenta =
            string.Equals(
                Environment.GetEnvironmentVariable("PROSPERISMO_FORCE_PIXEL_MAGENTA"),
                "1",
                StringComparison.Ordinal);

        // Which pixel-shader MRT export target (EXP_MRT0..7 == render-target
        // slot) is routed to the single fragment output. The offscreen draw
        // path renders one bound color target per pass, so a multi-render-target
        // (deferred G-buffer) draw compiles one pixel variant per slot, each
        // selecting that slot's export here.
        // Vertex stage only: the fragment shader paired with this vertex shader
        // declares interpolated inputs for locations 0..(this-1). Metal requires
        // every fragment input location to be written by the vertex shader, so
        // the vertex stage must export at least this many param outputs (any it
        // does not naturally export are zero-filled) or pipeline creation fails
        // with "Fragment input(s) `user(locnN)` ... not written by vertex shader".
        private readonly int _requiredVertexOutputCount;
        private readonly uint _localSizeX;
        private readonly uint _localSizeY;
        private readonly uint _localSizeZ;
        private readonly int _globalBufferBase;
        private readonly int _totalGlobalBufferCount;
        private readonly int _imageBindingBase;
        private readonly int _initialScalarBufferIndex;
        private readonly uint _pixelInputEnable;
        private readonly uint _pixelInputAddress;
        private readonly ulong _storageBufferOffsetAlignment;
        private readonly List<uint> _interfaces = [];
        private readonly Dictionary<uint, uint> _pixelInputs = [];
        private readonly Dictionary<uint, SpirvPixelOutput> _pixelOutputs = [];
        private readonly Dictionary<uint, uint> _vertexOutputs = [];
        private readonly Dictionary<uint, SpirvVertexInput> _vertexInputsByPc = [];
        private readonly List<SpirvImageResource> _imageResources = [];
        private readonly Dictionary<uint, int> _imageBindingByPc = [];
        private readonly Dictionary<uint, int> _bufferBindingByPc = [];
        private readonly Dictionary<uint, long[]> _scalarDefinitionsBeforePc = [];
        private readonly uint? _vertexPositionOutputControl;
        private readonly Gen5HostVertexOutputFeatures _hostVertexOutputFeatures;
        private readonly Gen5NggPrimitiveConnectivity? _nggPrimitiveConnectivity;
        private bool _sawNggPrimitiveExport;
        // POS1..POS3 component semantics, index 0 == EXP target 13. Empty until
        // DeclareStageInterface decodes PA_CL_VS_OUT_CNTL.
        private readonly Dictionary<uint, PositionOutputComponent[]> _positionSlots = [];
        // PCs whose SMEM buffer binding could not be recovered, so their scalar
        // destinations were zero-filled. Reported once at the end of the compile.
        private readonly SortedSet<uint> _zeroFilledScalarMemoryPcs = [];
        private uint _voidType;
        private uint _boolType;
        private uint _uintType;
        private uint _intType;
        private uint _longType;
        private uint _ulongType;
        private uint _floatType;
        private uint _vec2Type;
        private uint _vec3Type;
        private uint _vec4Type;
        private uint _uvec2Type;
        private uint _uvec3Type;
        private uint _uvec4Type;
        private uint _privateUintPointer;
        private uint _privateVec2Pointer;
        private uint _privateBoolPointer;
        private uint _runtimeBufferBiases;
        private uint _scalarRegisters;
        private uint _vectorRegisters;
        private uint _packedHalfRegisters;
        private uint _scc;
        private uint _vcc;
        private uint _exec;
        private uint _reachedPixelExport;
        private uint _programCounter;
        private uint _programActive;
        private uint _iterationGuard;
        private uint _globalBuffers;
        private uint _gfx10BufferFormatTable;
        private uint _storageBlockPointer;
        private uint _storageUintPointer;
        private uint _lds;
        private uint _ldsElementPointer;
        private uint _ldsDwordCount;
        private readonly uint _ldsDwordLimit;
        private readonly Gen5MergedWaveVgprSeeding? _mergedWaveSeeding;
        private readonly Gen5TessellationDomainSeeding? _domainSeeding;
        private uint _ldsOutOfRange;
        private uint _positionOutput;
        private uint _pointSizeOutput;
        private uint _layerOutput;
        private uint _viewportIndexOutput;
        private uint _clipDistanceOutput;
        private uint _cullDistanceOutput;
        private uint _clipDistanceCount;
        private uint _cullDistanceCount;
        private uint _vertexIndexInput;
        private uint _instanceIndexInput;
        private uint _fragCoordInput;
        private uint _localInvocationIdInput;
        private uint _localInvocationIndexInput;
        private uint _workGroupIdInput;
        private uint _computeDispatchLimit;
        private uint _pushConstantUintPointer;
        private uint _subgroupSizeInput;
        private uint _subgroupInvocationIdInput;
        private uint _waveMaskScratch;
        private uint _waveMaskScratchElementPointer;
        private uint _waveBroadcastScratch;
        private bool _waveScratchInLds;
        private uint _glsl;

        private enum ImageComponentKind
        {
            Float,
            Sint,
            Uint,
        }

        private enum VertexInputComponentKind
        {
            Float,
            Sint,
            Uint,
        }

        /// <summary>
        /// What one component of an EXP POS1..POS3 vector carries. The guest
        /// program only says "export four dwords to position vector N"; which
        /// system value each dword is comes entirely from PA_CL_VS_OUT_CNTL.
        /// </summary>
        private enum PositionOutputKind
        {
            None,
            PointSize,
            EdgeFlag,
            KillFlag,
            GsCutFlag,
            Layer,
            ViewportIndex,
            ClipDistance,
            CullDistance,
        }

        private readonly record struct PositionOutputComponent(
            PositionOutputKind Kind,
            uint Index)
        {
            public static PositionOutputComponent None => new(PositionOutputKind.None, 0);
        }

        private readonly record struct SpirvImageResource(
            uint Variable,
            uint ImageType,
            uint ObjectType,
            uint ComponentType,
            uint VectorType,
            ImageComponentKind ComponentKind,
            bool IsStorage,
            bool Arrayed);

        private readonly record struct SpirvVertexInput(
            uint Variable,
            uint Type,
            uint ComponentType,
            uint ComponentCount,
            VertexInputComponentKind ComponentKind);

        private readonly record struct SpirvPixelOutput(
            uint Variable,
            uint Type,
            Gen5PixelOutputKind Kind);

        public CompilationContext(
            Gen5SpirvStage stage,
            Gen5ShaderState state,
            Gen5ShaderEvaluation evaluation,
            IReadOnlyList<Gen5PixelOutputBinding> pixelOutputBindings,
            uint localSizeX,
            uint localSizeY,
            uint localSizeZ,
            int globalBufferBase,
            int totalGlobalBufferCount,
            int imageBindingBase,
            int initialScalarBufferIndex,
            uint pixelInputEnable = 0,
            uint pixelInputAddress = 0,
            int requiredVertexOutputCount = 0,
            uint waveLaneCount = 32,
            ulong storageBufferOffsetAlignment = 1,
            uint? vertexPositionOutputControl = null,
            Gen5HostVertexOutputFeatures hostVertexOutputFeatures = default,
            uint hostSubgroupSize = RdnaWaveLaneCount,
            uint ldsDwordLimit = LdsDwordCount,
            Gen5MergedWaveVgprSeeding? mergedWaveSeeding = null,
            Gen5TessellationDomainSeeding? domainSeeding = null,
            Gen5NggPrimitiveConnectivity? nggPrimitiveConnectivity = null)
        {
            _domainSeeding = domainSeeding;
            _mergedWaveSeeding = mergedWaveSeeding;
            _ldsDwordLimit = ldsDwordLimit == 0 ? LdsDwordCount : ldsDwordLimit;
            _stage = stage;
            _requiredVertexOutputCount = requiredVertexOutputCount;
            _state = state;
            _evaluation = evaluation;
            _pixelOutputBindings = pixelOutputBindings;
            _vertexPositionOutputControl = vertexPositionOutputControl;
            _hostVertexOutputFeatures = hostVertexOutputFeatures;
            if (nggPrimitiveConnectivity is { } connectivity && !connectivity.IsValid)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(nggPrimitiveConnectivity),
                    "NGG primitive connectivity must describe a finite non-indexed triangle draw");
            }

            _nggPrimitiveConnectivity = nggPrimitiveConnectivity;
            _hostSubgroupSize = hostSubgroupSize == 0
                ? RdnaWaveLaneCount
                : hostSubgroupSize;
            _waveLaneCount = waveLaneCount == 64 ? 64u : 32u;
            _emulateWave64 =
                stage == Gen5SpirvStage.Compute &&
                _waveLaneCount == 64 &&
                (ulong)localSizeX * localSizeY * localSizeZ == 64;
            _localSizeX = localSizeX;
            _localSizeY = localSizeY;
            _localSizeZ = localSizeZ;
            _globalBufferBase = globalBufferBase;
            _totalGlobalBufferCount = totalGlobalBufferCount < 0
                ? evaluation.GlobalMemoryBindings.Count
                : totalGlobalBufferCount;
            _imageBindingBase = imageBindingBase;
            _initialScalarBufferIndex = initialScalarBufferIndex;
            _pixelInputEnable = pixelInputEnable;
            _pixelInputAddress = pixelInputAddress;
            if (storageBufferOffsetAlignment == 0 ||
                (storageBufferOffsetAlignment & (storageBufferOffsetAlignment - 1)) != 0 ||
                storageBufferOffsetAlignment > uint.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(storageBufferOffsetAlignment),
                    storageBufferOffsetAlignment,
                    "storage-buffer offset alignment must be a uint-sized power of two");
            }

            _storageBufferOffsetAlignment = storageBufferOffsetAlignment;
        }

        public bool TryCompile(out Gen5SpirvShader shader, out string error)
        {
            shader = default!;
            error = string.Empty;
            try
            {
                if (!ValidateWaveWidthIsModelled(out error))
                {
                    return false;
                }

                if (Environment.GetEnvironmentVariable(
                        "PROSPERISMO_TRACE_TITLE_INTERFACE") == "1" &&
                    _state.Program.Address is 0x0000000500780000ul or
                        0x0000000500781200ul)
                {
                    Console.Error.WriteLine(
                        $"[AGC][TITLE-INTERFACE] stage={_stage} " +
                        $"address=0x{_state.Program.Address:X16} " +
                        $"required_vertex_outputs={_requiredVertexOutputCount} " +
                        $"ps_ena=0x{_pixelInputEnable:X8} ps_addr=0x{_pixelInputAddress:X8}");
                    foreach (var instruction in _state.Program.Instructions)
                    {
                        if (instruction.Control is Gen5ExportControl export)
                        {
                            Console.Error.WriteLine(
                                $"[AGC][TITLE-INTERFACE] pc=0x{instruction.Pc:X4} " +
                                $"export_target={export.Target} mask=0x{export.EnableMask:X} " +
                                $"compressed={export.Compressed} src=[" +
                                string.Join(',', instruction.Sources) + "]");
                        }
                        else if (instruction.Control is Gen5InterpolationControl interpolation)
                        {
                            Console.Error.WriteLine(
                                $"[AGC][TITLE-INTERFACE] pc=0x{instruction.Pc:X4} " +
                                $"attribute={interpolation.Attribute} " +
                                $"channel={interpolation.Channel} dst=[" +
                                string.Join(',', instruction.Destinations) + "]");
                        }
                        else if (instruction.Control is Gen5ScalarMemoryControl scalarMemory)
                        {
                            var bindingIndex = -1;
                            for (var index = 0;
                                 index < _evaluation.GlobalMemoryBindings.Count;
                                 index++)
                            {
                                if (_evaluation.GlobalMemoryBindings[index]
                                    .InstructionPcs.Contains(instruction.Pc))
                                {
                                    bindingIndex = index;
                                    break;
                                }
                            }

                            var binding = bindingIndex >= 0
                                ? _evaluation.GlobalMemoryBindings[bindingIndex]
                                : null;
                            var byteOffset = scalarMemory.ImmediateOffsetBytes;
                            var sample = binding is not null &&
                                         scalarMemory.DynamicOffsetRegister is null &&
                                         byteOffset >= 0 &&
                                         byteOffset < binding.DataLength
                                ? Convert.ToHexString(
                                    binding.Data.AsSpan(
                                        byteOffset,
                                        Math.Min(
                                            checked((int)scalarMemory.DestinationCount * 4),
                                            binding.DataLength - byteOffset)))
                                : "dynamic-or-unavailable";
                            Console.Error.WriteLine(
                                $"[AGC][TITLE-INTERFACE] pc=0x{instruction.Pc:X4} " +
                                $"scalar_load={instruction.Opcode} binding={bindingIndex} " +
                                $"scalar=s{binding?.ScalarAddress} " +
                                $"base=0x{binding?.BaseAddress:X16} length={binding?.DataLength} " +
                                $"offset={byteOffset} dynamic={scalarMemory.DynamicOffsetRegister} " +
                                $"bytes={sample}");
                        }
                    }
                }

                DeclareModule();
                var blocks = BuildBasicBlocks(_state.Program.Instructions);
                if (blocks.Count == 0)
                {
                    error = "shader contains no executable blocks";
                    return false;
                }

                BuildScalarDefinitionInfo(blocks, _state.Program.Instructions);

                var functionType = _module.TypeFunction(_voidType);
                var main = _module.BeginFunction(_voidType, functionType);
                _module.AddName(main, "main");
                _module.AddLabel();
                if (_stage == Gen5SpirvStage.Pixel &&
                    Environment.GetEnvironmentVariable(
                        "PROSPERISMO_FORCE_TITLE_EARLY_COLOR") == "1" &&
                    _state.Program.Address == 0x0000000500781200ul)
                {
                    var earlyOutput = _pixelOutputs
                        .OrderBy(static pair => pair.Key)
                        .Select(static pair => pair.Value)
                        .First();
                    var earlyColor = earlyOutput.Kind switch
                    {
                        Gen5PixelOutputKind.Float =>
                            _module.AddInstruction(
                                SpirvOp.CompositeConstruct,
                                earlyOutput.Type,
                                Float(1f),
                                Float(0f),
                                Float(1f),
                                Float(1f)),
                        Gen5PixelOutputKind.Sint =>
                            _module.ConstantNull(earlyOutput.Type),
                        _ => _module.ConstantNull(earlyOutput.Type),
                    };
                    Store(earlyOutput.Variable, earlyColor);
                    _module.AddStatement(SpirvOp.Return);
                    _module.AddLabel();
                }
                EmitInitialState();

                var loopHeader = _module.AllocateId();
                var switchHeader = _module.AllocateId();
                var switchMerge = _module.AllocateId();
                var loopContinue = _module.AllocateId();
                var loopMerge = _module.AllocateId();
                var defaultLabel = _module.AllocateId();
                var caseLabels = new uint[blocks.Count];
                for (var index = 0; index < caseLabels.Length; index++)
                {
                    caseLabels[index] = _module.AllocateId();
                }

                _module.AddStatement(SpirvOp.Branch, loopHeader);
                _module.AddLabel(loopHeader);
                _module.AddStatement(SpirvOp.LoopMerge, loopMerge, loopContinue, 0);
                _module.AddStatement(SpirvOp.Branch, switchHeader);

                _module.AddLabel(switchHeader);
                var selector = Load(_uintType, _programCounter);
                _module.AddStatement(SpirvOp.SelectionMerge, switchMerge, 0);
                var switchOperands = new uint[2 + (blocks.Count * 2)];
                switchOperands[0] = selector;
                switchOperands[1] = defaultLabel;
                for (var index = 0; index < blocks.Count; index++)
                {
                    switchOperands[2 + (index * 2)] = (uint)index;
                    switchOperands[3 + (index * 2)] = caseLabels[index];
                }

                _module.AddStatement(SpirvOp.Switch, switchOperands);
                for (var index = 0; index < blocks.Count; index++)
                {
                    _module.AddLabel(caseLabels[index]);
                    if (!TryEmitBlock(blocks, index, out error))
                    {
                        error = $"block=0x{blocks[index].StartPc:X}: {error}";
                        return false;
                    }

                    _module.AddStatement(SpirvOp.Branch, switchMerge);
                }

                _module.AddLabel(defaultLabel);
                Store(_programActive, _module.ConstantBool(false));
                _module.AddStatement(SpirvOp.Branch, switchMerge);

                _module.AddLabel(switchMerge);
                _module.AddStatement(SpirvOp.Branch, loopContinue);
                _module.AddLabel(loopContinue);
                var active = Load(_boolType, _programActive);
                if (_maxDispatcherSteps > 0)
                {
                    var steps = IAdd(Load(_uintType, _iterationGuard), UInt(1));
                    Store(_iterationGuard, steps);
                    var withinLimit = _module.AddInstruction(
                        SpirvOp.ULessThan,
                        _boolType,
                        steps,
                        UInt((uint)_maxDispatcherSteps));
                    active = _module.AddInstruction(
                        SpirvOp.LogicalAnd,
                        _boolType,
                        active,
                        withinLimit);
                }

                _module.AddStatement(
                    SpirvOp.BranchConditional,
                    active,
                    loopHeader,
                    loopMerge);
                _module.AddLabel(loopMerge);
                if (_stage == Gen5SpirvStage.Pixel &&
                    Environment.GetEnvironmentVariable(
                        "PROSPERISMO_TRACE_TITLE_SHADER_STATE") == "1" &&
                    _state.Program.Address == 0x0000000500781200ul)
                {
                    var stateOutput = _pixelOutputs
                        .OrderBy(static pair => pair.Key)
                        .Select(static pair => pair.Value)
                        .FirstOrDefault(static output =>
                            output.Kind == Gen5PixelOutputKind.Float);
                    if (stateOutput.Variable != 0)
                    {
                        uint EncodeBool(uint condition) =>
                            _module.AddInstruction(
                                SpirvOp.Select,
                                _floatType,
                                condition,
                                Float(1f),
                                Float(0f));

                        Store(
                            stateOutput.Variable,
                            _module.AddInstruction(
                                SpirvOp.CompositeConstruct,
                                stateOutput.Type,
                                EncodeBool(Load(_boolType, _exec)),
                                EncodeBool(IsWaveMaskActive(LoadS64(52))),
                                EncodeBool(Load(_boolType, _reachedPixelExport)),
                                Float(1f)));
                    }

                    StoreS64(
                        126,
                        _module.Constant64(_ulongType, 1));
                }
                if (_stage == Gen5SpirvStage.Pixel)
                {
                    // A fragment lane removed from EXEC is not a request to
                    // write the output variable's zero initializer. It is a
                    // killed fragment and must not participate in color,
                    // depth, or blend operations. Keep EXEC masking during
                    // translation, then terminate lanes that remain inactive
                    // when the guest pixel shader exits.
                    var returnLabel = _module.AllocateId();
                    var killLabel = _module.AllocateId();
                    // Materialize the condition before SelectionMerge: SPIR-V
                    // requires the merge instruction to be immediately followed
                    // by its structured branch terminator.
                    var laneActive = Load(_boolType, _exec);
                    _module.AddStatement(
                        SpirvOp.SelectionMerge,
                        returnLabel,
                        0);
                    _module.AddStatement(
                        SpirvOp.BranchConditional,
                        laneActive,
                        returnLabel,
                        killLabel);
                    _module.AddLabel(killLabel);
                    _module.AddStatement(SpirvOp.Kill);
                    _module.AddLabel(returnLabel);
                }

                _module.AddStatement(SpirvOp.Return);
                _module.EndFunction();

                var model = _stage switch
                {
                    Gen5SpirvStage.Vertex => SpirvExecutionModel.Vertex,
                    Gen5SpirvStage.Pixel => SpirvExecutionModel.Fragment,
                    _ => SpirvExecutionModel.GLCompute,
                };
                _module.AddEntryPoint(model, main, "main", _interfaces);
                if (_stage == Gen5SpirvStage.Pixel)
                {
                    _module.AddExecutionMode(main, SpirvExecutionMode.OriginUpperLeft);
                }
                else if (_stage == Gen5SpirvStage.Compute)
                {
                    _module.AddExecutionMode(
                        main,
                        SpirvExecutionMode.LocalSize,
                        _localSizeX,
                        _localSizeY,
                        _localSizeZ);
                }

                ReportZeroFilledScalarMemory();

                if (_nggPrimitiveConnectivity is not null && !_sawNggPrimitiveExport)
                {
                    error = "host NGG primitive connectivity was supplied, but the vertex shader has no EXP target 20";
                    return false;
                }

                var attributeCount = _stage == Gen5SpirvStage.Vertex
                    ? (uint)_vertexOutputs.Count
                    : (uint)_pixelInputs.Count;
                shader = new Gen5SpirvShader(
                    _module.Build(),
                    _evaluation.GlobalMemoryBindings,
                    _evaluation.ImageBindings,
                    attributeCount,
                    _stage == Gen5SpirvStage.Vertex
                        ? _evaluation.VertexInputs ?? []
                        : [],
                    _sawNggPrimitiveExport ? _nggPrimitiveConnectivity : null);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private void DeclareModule()
        {
            _module.AddCapability(SpirvCapability.Shader);
            _module.AddCapability(SpirvCapability.Int64);
            _module.AddCapability(SpirvCapability.ImageQuery);
            if (_evaluation.ImageBindings.Any(
                    static binding =>
                        (binding.Opcode.StartsWith(
                             "ImageSample",
                             StringComparison.Ordinal) ||
                         binding.Opcode.StartsWith(
                             "ImageGather4",
                             StringComparison.Ordinal)) &&
                        binding.Opcode.EndsWith("O", StringComparison.Ordinal)))
            {
                _module.AddCapability(SpirvCapability.ImageGatherExtended);
            }

            if (UsesSubgroupOperations())
            {
                _module.AddCapability(SpirvCapability.GroupNonUniform);
                if (UsesSubgroupShuffle())
                {
                    _module.AddCapability(SpirvCapability.GroupNonUniformShuffle);
                }

                if (UsesWaveControl())
                {
                    _module.AddCapability(SpirvCapability.GroupNonUniformVote);
                }

                // Declared for EVERY subgroup-using module, not just the broadcast and
                // wave-control ones. BooleanToWaveMask emits OpGroupNonUniformBallot on the
                // sole condition that _subgroupInvocationIdInput exists, and that input is
                // declared by DeclareStageInterface under exactly this same
                // UsesSubgroupOperations() test. So any path that reaches this block can
                // reach the ballot: the old subset condition left the DS crossbar shuffles
                // and the v_mbcnt pair emitting an opcode whose capability was never
                // declared, and spirv-val rejects the whole module for it ("Opcode
                // GroupNonUniformBallot requires one of these capabilities:
                // GroupNonUniformBallot"). A strict driver drops the pipeline; a lenient one
                // takes it, which is how this stayed invisible.
                //
                // Declaring a capability the module happens not to use is valid SPIR-V, so
                // matching the precondition exactly is the safe direction to err in.
                _module.AddCapability(SpirvCapability.GroupNonUniformBallot);
            }

            _glsl = _module.ImportExtInst("GLSL.std.450");
            _voidType = _module.TypeVoid();
            _boolType = _module.TypeBool();
            _uintType = _module.TypeInt(32, signed: false);
            _intType = _module.TypeInt(32, signed: true);
            _longType = _module.TypeInt(64, signed: true);
            _ulongType = _module.TypeInt(64, signed: false);
            _floatType = _module.TypeFloat(32);
            _vec2Type = _module.TypeVector(_floatType, 2);
            _vec3Type = _module.TypeVector(_floatType, 3);
            _vec4Type = _module.TypeVector(_floatType, 4);
            _uvec2Type = _module.TypeVector(_uintType, 2);
            _uvec3Type = _module.TypeVector(_uintType, 3);
            _uvec4Type = _module.TypeVector(_uintType, 4);
            _privateUintPointer =
                _module.TypePointer(SpirvStorageClass.Private, _uintType);
            _privateVec2Pointer =
                _module.TypePointer(SpirvStorageClass.Private, _vec2Type);
            _privateBoolPointer =
                _module.TypePointer(SpirvStorageClass.Private, _boolType);

            var scalarArrayType = _module.TypeArray(_uintType, ScalarRegisterCount);
            var vectorArrayType = _module.TypeArray(_uintType, VectorRegisterCount);
            var packedHalfArrayType = _module.TypeArray(_vec2Type, VectorRegisterCount);
            var privateScalarArrayPointer =
                _module.TypePointer(SpirvStorageClass.Private, scalarArrayType);
            var privateVectorArrayPointer =
                _module.TypePointer(SpirvStorageClass.Private, vectorArrayType);
            var privatePackedHalfArrayPointer =
                _module.TypePointer(SpirvStorageClass.Private, packedHalfArrayType);
            _scalarRegisters = _module.AddGlobalVariable(
                privateScalarArrayPointer,
                SpirvStorageClass.Private,
                _module.ConstantNull(scalarArrayType));
            _vectorRegisters = _module.AddGlobalVariable(
                privateVectorArrayPointer,
                SpirvStorageClass.Private,
                _module.ConstantNull(vectorArrayType));
            _packedHalfRegisters = _module.AddGlobalVariable(
                privatePackedHalfArrayPointer,
                SpirvStorageClass.Private,
                _module.ConstantNull(packedHalfArrayType));
            _scc = _module.AddGlobalVariable(
                _privateBoolPointer,
                SpirvStorageClass.Private,
                _module.ConstantBool(false));
            _vcc = _module.AddGlobalVariable(
                _privateBoolPointer,
                SpirvStorageClass.Private,
                _module.ConstantBool(false));
            _exec = _module.AddGlobalVariable(
                _privateBoolPointer,
                SpirvStorageClass.Private,
                _module.ConstantBool(true));
            _reachedPixelExport = _module.AddGlobalVariable(
                _privateBoolPointer,
                SpirvStorageClass.Private,
                _module.ConstantBool(false));
            _programCounter = _module.AddGlobalVariable(
                _privateUintPointer,
                SpirvStorageClass.Private,
                _module.Constant(_uintType, 0));
            _programActive = _module.AddGlobalVariable(
                _privateBoolPointer,
                SpirvStorageClass.Private,
                _module.ConstantBool(true));
            if (_maxDispatcherSteps > 0)
            {
                _iterationGuard = _module.AddGlobalVariable(
                    _privateUintPointer,
                    SpirvStorageClass.Private,
                    _module.Constant(_uintType, 0));
                _interfaces.Add(_iterationGuard);
                _module.AddName(_iterationGuard, "pcGuard");
            }

            _interfaces.Add(_scalarRegisters);
            _interfaces.Add(_vectorRegisters);
            _interfaces.Add(_packedHalfRegisters);
            _interfaces.Add(_scc);
            _interfaces.Add(_vcc);
            _interfaces.Add(_exec);
            _interfaces.Add(_reachedPixelExport);
            _interfaces.Add(_programCounter);
            _interfaces.Add(_programActive);
            _module.AddName(_scalarRegisters, "sgpr");
            _module.AddName(_vectorRegisters, "vgpr");
            _module.AddName(_packedHalfRegisters, "vgprPackedHalf");

            var runtimeBufferBiasCount =
                _globalBufferBase + _evaluation.GlobalMemoryBindings.Count;
            if (_initialScalarBufferIndex >= 0 && runtimeBufferBiasCount > 0)
            {
                var biasArrayType = _module.TypeArray(
                    _uintType,
                    (uint)runtimeBufferBiasCount);
                var privateBiasArrayPointer = _module.TypePointer(
                    SpirvStorageClass.Private,
                    biasArrayType);
                _runtimeBufferBiases = _module.AddGlobalVariable(
                    privateBiasArrayPointer,
                    SpirvStorageClass.Private,
                    _module.ConstantNull(biasArrayType));
                _module.AddName(_runtimeBufferBiases, "guestBufferByteBias");
                _interfaces.Add(_runtimeBufferBiases);
            }

            DeclareBuffers();
            DeclareImages();
            DeclareLds();
            DeclareWave64Scratch();
            DeclareStageInterface();
            DeclareComputeDispatchLimit();
        }

        private void DeclareComputeDispatchLimit()
        {
            if (_stage != Gen5SpirvStage.Compute)
            {
                return;
            }

            // RDNA DISPATCH_* can express exact thread dimensions, including
            // a final partially populated workgroup. Vulkan dispatches whole
            // workgroups, so the command path supplies the exact exclusive
            // thread bounds through a small push-constant block and excess
            // invocations are disabled before any guest instruction executes.
            var block = _module.TypeStruct(_uvec3Type);
            _module.AddDecoration(block, SpirvDecoration.Block);
            _module.AddMemberDecoration(block, 0, SpirvDecoration.Offset, 0);
            var blockPointer =
                _module.TypePointer(SpirvStorageClass.PushConstant, block);
            _pushConstantUintPointer =
                _module.TypePointer(SpirvStorageClass.PushConstant, _uintType);
            _computeDispatchLimit = _module.AddGlobalVariable(
                blockPointer,
                SpirvStorageClass.PushConstant);
            _module.AddName(_computeDispatchLimit, "dispatchThreadLimit");
            _interfaces.Add(_computeDispatchLimit);
        }

        private void DeclareWave64Scratch()
        {
            if (!_emulateWave64 || !UsesSubgroupOperations())
            {
                return;
            }

            // Metal exposes 32 KiB of threadgroup memory on the Apple GPUs we
            // target. Some PS5 compute shaders legitimately request all of it,
            // so allocating another workgroup variable for the wave64 bridge
            // makes pipeline creation fail. Reuse the final three dwords of the
            // existing LDS allocation in that case. The translator already
            // bounds guest LDS accesses to this fixed allocation; keeping the
            // bridge inside it preserves the host limit and still provides the
            // cross-subgroup rendezvous needed to model one 64-lane guest wave.
            if (_lds != 0)
            {
                _waveScratchInLds = true;
                _waveMaskScratchElementPointer = _ldsElementPointer;
                return;
            }

            var maskArrayType = _module.TypeArray(_uintType, 2);
            var maskArrayPointer =
                _module.TypePointer(SpirvStorageClass.Workgroup, maskArrayType);
            _waveMaskScratchElementPointer =
                _module.TypePointer(SpirvStorageClass.Workgroup, _uintType);
            _waveMaskScratch = _module.AddGlobalVariable(
                maskArrayPointer,
                SpirvStorageClass.Workgroup);
            _module.AddName(_waveMaskScratch, "wave64MaskScratch");
            _interfaces.Add(_waveMaskScratch);

            var uintPointer =
                _module.TypePointer(SpirvStorageClass.Workgroup, _uintType);
            _waveBroadcastScratch = _module.AddGlobalVariable(
                uintPointer,
                SpirvStorageClass.Workgroup);
            _module.AddName(_waveBroadcastScratch, "wave64BroadcastScratch");
            _interfaces.Add(_waveBroadcastScratch);
        }

        private void DeclareLds()
        {
            if (!UsesLds())
            {
                return;
            }

            // Compute shaders get genuine workgroup-shared LDS. Graphics stages
            // (NGG export/vertex, pixel) cannot use the Workgroup storage class
            // in SPIR-V, but they still emit ds_write/ds_read, typically as
            // per-invocation scratch/spill or as NGG staging whose cross-lane
            // reads don't feed this stage's exports. Model those as a
            // per-invocation Private array so the shader is valid SPIR-V and its
            // draw stops being dropped. LdsPointer range-checks computed
            // addresses instead of aliasing an invalid address into the array.
            var storageClass = _stage == Gen5SpirvStage.Compute
                ? SpirvStorageClass.Workgroup
                : SpirvStorageClass.Private;
            var dwordCount = _stage == Gen5SpirvStage.Compute
                ? Math.Min(_ldsDwordLimit, LdsDwordCount)
                : PrivateLdsDwordCount;
            _ldsDwordCount = dwordCount;

            var ldsArrayType = _module.TypeArray(_uintType, dwordCount);
            var ldsPointer = _module.TypePointer(storageClass, ldsArrayType);
            _ldsElementPointer = _module.TypePointer(storageClass, _uintType);
            _lds = storageClass == SpirvStorageClass.Workgroup
                ? _module.AddGlobalVariable(ldsPointer, storageClass)
                : _module.AddGlobalVariable(
                    ldsPointer,
                    storageClass,
                    _module.ConstantNull(ldsArrayType));
            _module.AddName(_lds, "lds");
            _interfaces.Add(_lds);
            _ldsOutOfRange = _module.AddGlobalVariable(
                _privateBoolPointer,
                SpirvStorageClass.Private,
                _module.ConstantBool(false));
            _module.AddName(_ldsOutOfRange, "ldsAddressOutOfRange");
            _interfaces.Add(_ldsOutOfRange);
            ReportDiagnosticOnce(
                $"lds-runtime-range-check:0x{_state.Program.Address:X16}",
                $"[SPIRV][WARN] program=0x{_state.Program.Address:X16} uses LDS. " +
                "Runtime addresses are checked against the declared allocation; " +
                "the named module flag ldsAddressOutOfRange records invalid " +
                "active-lane accesses, whose reads return zero and writes are discarded.");
        }

        private void DeclareBuffers()
        {
            for (var index = 0; index < _evaluation.GlobalMemoryBindings.Count; index++)
            {
                foreach (var pc in _evaluation.GlobalMemoryBindings[index].InstructionPcs)
                {
                    _bufferBindingByPc.TryAdd(pc, _globalBufferBase + index);
                }
            }

            if (_totalGlobalBufferCount == 0)
            {
                return;
            }

            var runtimeArray = _module.TypeRuntimeArray(_uintType);
            _module.AddDecoration(runtimeArray, SpirvDecoration.ArrayStride, sizeof(uint));
            var block = _module.TypeStruct(runtimeArray);
            _module.AddDecoration(block, SpirvDecoration.Block);
            _module.AddMemberDecoration(block, 0, SpirvDecoration.Offset, 0);
            var descriptors = _module.TypeArray(
                block,
                (uint)_totalGlobalBufferCount);
            var descriptorsPointer =
                _module.TypePointer(SpirvStorageClass.StorageBuffer, descriptors);
            _storageBlockPointer =
                _module.TypePointer(SpirvStorageClass.StorageBuffer, block);
            _storageUintPointer =
                _module.TypePointer(SpirvStorageClass.StorageBuffer, _uintType);
            _globalBuffers = _module.AddGlobalVariable(
                descriptorsPointer,
                SpirvStorageClass.StorageBuffer);
            _module.AddName(_globalBuffers, "guestBuffers");
            _module.AddDecoration(_globalBuffers, SpirvDecoration.DescriptorSet, 0);
            _module.AddDecoration(_globalBuffers, SpirvDecoration.Binding, 0);
            _interfaces.Add(_globalBuffers);
        }

        private void DeclareImages()
        {
            for (var index = 0; index < _evaluation.ImageBindings.Count; index++)
            {
                var binding = _evaluation.ImageBindings[index];
                _imageBindingByPc.TryAdd(binding.Pc, index);
                var isStorage = Gen5ShaderTranslator.RequiresStorageImage(
                    binding,
                    _evaluation.ImageBindings);
                var (format, componentKind) =
                    DecodeImageFormat(binding.ResourceDescriptor);
                var componentType = componentKind switch
                {
                    ImageComponentKind.Sint => _intType,
                    ImageComponentKind.Uint => _uintType,
                    _ => _floatType,
                };
                if (isStorage && format == SpirvImageFormat.Unknown)
                {
                    _module.AddCapability(
                        SpirvCapability.StorageImageReadWithoutFormat);
                    _module.AddCapability(
                        SpirvCapability.StorageImageWriteWithoutFormat);
                }
                else if (isStorage && RequiresExtendedStorageImageFormat(format))
                {
                    _module.AddCapability(
                        SpirvCapability.StorageImageExtendedFormats);
                }

                var isArrayed = !isStorage &&
                    Gen5ShaderTranslator.IsArrayedImageBinding(binding);
                var imageType = _module.TypeImage(
                    componentType,
                    SpirvImageDim.Dim2D,
                    depth: false,
                    arrayed: isArrayed,
                    multisampled: false,
                    sampled: isStorage ? 2u : 1u,
                    isStorage ? format : SpirvImageFormat.Unknown);
                var objectType = isStorage
                    ? imageType
                    : _module.TypeSampledImage(imageType);
                var pointer = _module.TypePointer(
                    SpirvStorageClass.UniformConstant,
                    objectType);
                var variable = _module.AddGlobalVariable(
                    pointer,
                    SpirvStorageClass.UniformConstant);
                _module.AddName(variable, isStorage ? $"image{index}" : $"tex{index}");
                _module.AddDecoration(variable, SpirvDecoration.DescriptorSet, 0);
                _module.AddDecoration(
                    variable,
                    SpirvDecoration.Binding,
                    (uint)(_imageBindingBase + index + 1));
                _imageResources.Add(
                    new SpirvImageResource(
                        variable,
                        imageType,
                        objectType,
                        componentType,
                        _module.TypeVector(componentType, 4),
                        componentKind,
                        isStorage,
                        isArrayed));
                _interfaces.Add(variable);
            }
        }

        private static bool RequiresExtendedStorageImageFormat(
            SpirvImageFormat format) =>
            format is not SpirvImageFormat.Unknown and
                not SpirvImageFormat.Rgba32f and
                not SpirvImageFormat.Rgba32i and
                not SpirvImageFormat.Rgba32ui;

        private static (SpirvImageFormat Format, ImageComponentKind Kind)
            DecodeImageFormat(IReadOnlyList<uint> descriptor)
        {
            if (descriptor.Count < 2)
            {
                return (SpirvImageFormat.Unknown, ImageComponentKind.Float);
            }

            var unifiedFormat = (descriptor[1] >> 20) & 0x1FFu;
            if (!Gfx10UnifiedFormat.TryDecode(
                    unifiedFormat,
                    out var dataFormat,
                    out var numberType))
            {
                return (SpirvImageFormat.Unknown, ImageComponentKind.Float);
            }

            var kind = numberType switch
            {
                4 => ImageComponentKind.Uint,
                5 => ImageComponentKind.Sint,
                _ => ImageComponentKind.Float,
            };
            return (DecodeStorageImageFormat(dataFormat, numberType), kind);
        }

        internal static SpirvImageFormat DecodeStorageImageFormat(
            uint dataFormat,
            uint numberType) =>
            (dataFormat, numberType) switch
            {
                (1, 0 or 9) => SpirvImageFormat.R8,
                (1, 1) => SpirvImageFormat.R8Snorm,
                (1, 4) => SpirvImageFormat.R8ui,
                (1, 5) => SpirvImageFormat.R8i,
                (2, 0) => SpirvImageFormat.R16,
                (2, 1) => SpirvImageFormat.R16Snorm,
                (2, 4) => SpirvImageFormat.R16ui,
                (2, 5) => SpirvImageFormat.R16i,
                (2, 7) => SpirvImageFormat.R16f,
                (3, 0 or 9) => SpirvImageFormat.Rg8,
                (3, 1) => SpirvImageFormat.Rg8Snorm,
                (3, 4) => SpirvImageFormat.Rg8ui,
                (3, 5) => SpirvImageFormat.Rg8i,
                (4, 4) => SpirvImageFormat.R32ui,
                (4, 5) => SpirvImageFormat.R32i,
                (4, 7) => SpirvImageFormat.R32f,
                (5, 0) => SpirvImageFormat.Rg16,
                (5, 1) => SpirvImageFormat.Rg16Snorm,
                (5, 4) => SpirvImageFormat.Rg16ui,
                (5, 5) => SpirvImageFormat.Rg16i,
                (5, 7) => SpirvImageFormat.Rg16f,
                (6 or 7, 7) => SpirvImageFormat.R11fG11fB10f,
                (8 or 9, 0) => SpirvImageFormat.Rgb10A2,
                (8 or 9, 4) => SpirvImageFormat.Rgb10A2ui,
                (10, 0 or 9) => SpirvImageFormat.Rgba8,
                (10, 1) => SpirvImageFormat.Rgba8Snorm,
                (10, 4) => SpirvImageFormat.Rgba8ui,
                (10, 5) => SpirvImageFormat.Rgba8i,
                (11, 4) => SpirvImageFormat.Rg32ui,
                (11, 5) => SpirvImageFormat.Rg32i,
                (11, 7) => SpirvImageFormat.Rg32f,
                (12, 0) => SpirvImageFormat.Rgba16,
                (12, 1) => SpirvImageFormat.Rgba16Snorm,
                (12, 4) => SpirvImageFormat.Rgba16ui,
                (12, 5) => SpirvImageFormat.Rgba16i,
                (12, 7) => SpirvImageFormat.Rgba16f,
                (13 or 14, 4) => SpirvImageFormat.Rgba32ui,
                (13 or 14, 5) => SpirvImageFormat.Rgba32i,
                (13 or 14, 7) => SpirvImageFormat.Rgba32f,
                _ => SpirvImageFormat.Unknown,
            };

        private void DeclareStageInterface()
        {
            if (UsesSubgroupOperations())
            {
                var subgroupPointer =
                    _module.TypePointer(SpirvStorageClass.Input, _uintType);
                _subgroupInvocationIdInput = _module.AddGlobalVariable(
                    subgroupPointer,
                    SpirvStorageClass.Input);
                _module.AddDecoration(
                    _subgroupInvocationIdInput,
                    SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.SubgroupLocalInvocationId);
                if (_stage == Gen5SpirvStage.Pixel)
                {
                    // Vulkan requires integer fragment inputs, including this
                    // subgroup built-in, to carry Flat interpolation.
                    _module.AddDecoration(
                        _subgroupInvocationIdInput,
                        SpirvDecoration.Flat);
                }
                _interfaces.Add(_subgroupInvocationIdInput);

                if (_emulateWave64)
                {
                    _subgroupSizeInput = _module.AddGlobalVariable(
                        subgroupPointer,
                        SpirvStorageClass.Input);
                    _module.AddDecoration(
                        _subgroupSizeInput,
                        SpirvDecoration.BuiltIn,
                        (uint)SpirvBuiltIn.SubgroupSize);
                    _interfaces.Add(_subgroupSizeInput);
                }

                if (_waveLaneCount == 64)
                {
                    _localInvocationIndexInput = _module.AddGlobalVariable(
                        subgroupPointer,
                        SpirvStorageClass.Input);
                    _module.AddDecoration(
                        _localInvocationIndexInput,
                        SpirvDecoration.BuiltIn,
                        (uint)SpirvBuiltIn.LocalInvocationIndex);
                    _interfaces.Add(_localInvocationIndexInput);
                }

                ReportHostSubgroupSizeMismatch();
            }

            if (_stage == Gen5SpirvStage.Vertex)
            {
                DeclareVertexInputs();

                var inputPointer =
                    _module.TypePointer(SpirvStorageClass.Input, _uintType);
                _vertexIndexInput = _module.AddGlobalVariable(
                    inputPointer,
                    SpirvStorageClass.Input);
                _module.AddDecoration(
                    _vertexIndexInput,
                    SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.VertexIndex);
                _interfaces.Add(_vertexIndexInput);

                _instanceIndexInput = _module.AddGlobalVariable(
                    inputPointer,
                    SpirvStorageClass.Input);
                _module.AddDecoration(
                    _instanceIndexInput,
                    SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.InstanceIndex);
                _interfaces.Add(_instanceIndexInput);

                var outputPointer =
                    _module.TypePointer(SpirvStorageClass.Output, _vec4Type);
                _positionOutput = _module.AddGlobalVariable(
                    outputPointer,
                    SpirvStorageClass.Output);
                _module.AddDecoration(
                    _positionOutput,
                    SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.Position);
                _interfaces.Add(_positionOutput);

                DeclareVertexPositionAuxiliaryOutputs();

                var parameters = _state.Program.Instructions
                    .Select(instruction => instruction.Control)
                    .OfType<Gen5ExportControl>()
                    .Where(export => export.Target is >= 32 and < 64)
                    .Select(export => export.Target - 32)
                    // Cover every location the paired fragment shader reads, even
                    // ones this vertex program never exports, so Metal's exact
                    // vertex-out/fragment-in interface match succeeds. Extras are
                    // zero-filled in EmitInitialState.
                    .Concat(Enumerable
                        .Range(0, Math.Max(_requiredVertexOutputCount, 0))
                        .Select(location => (uint)location))
                    .Distinct()
                    .Order()
                    .ToArray();
                foreach (var parameter in parameters)
                {
                    var variable = _module.AddGlobalVariable(
                        outputPointer,
                        SpirvStorageClass.Output);
                    _module.AddDecoration(variable, SpirvDecoration.Location, parameter);
                    _vertexOutputs.Add(parameter, variable);
                    _interfaces.Add(variable);
                }
            }
            else if (_stage == Gen5SpirvStage.Pixel)
            {
                var inputVec4Pointer =
                    _module.TypePointer(SpirvStorageClass.Input, _vec4Type);
                var interpolations = _state.Program.Instructions
                    .Where(static instruction =>
                        instruction.Control is Gen5InterpolationControl)
                    .ToArray();
                var attributes = interpolations
                    .Select(instruction =>
                        ((Gen5InterpolationControl)instruction.Control!).Attribute)
                    .Distinct()
                    .Order()
                    .ToArray();
                foreach (var attribute in attributes)
                {
                    var variable = _module.AddGlobalVariable(
                        inputVec4Pointer,
                        SpirvStorageClass.Input);
                    _module.AddDecoration(variable, SpirvDecoration.Location, attribute);
                    // v_interp_mov_f32 reads the constant-interpolation
                    // integer bits (the particle id) through a float export;
                    // smooth interpolation turns those subnormal bit patterns
                    // into zero before the fragment shader can bitcast them.
                    if (interpolations
                        .Where(instruction =>
                            ((Gen5InterpolationControl)instruction.Control!).Attribute == attribute)
                        .All(static instruction => instruction.Opcode == "VInterpMovF32"))
                    {
                        _module.AddDecoration(variable, SpirvDecoration.Flat);
                    }
                    _pixelInputs.Add(attribute, variable);
                    _interfaces.Add(variable);
                }

                _fragCoordInput = _module.AddGlobalVariable(
                    inputVec4Pointer,
                    SpirvStorageClass.Input);
                _module.AddDecoration(
                    _fragCoordInput,
                    SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.FragCoord);
                _interfaces.Add(_fragCoordInput);

                var declaredPixelOutputs =
                    Environment.GetEnvironmentVariable(
                        "PROSPERISMO_FORCE_TITLE_SINGLE_MRT") == "1" &&
                    _state.Program.Address == 0x0000000500781200ul
                        ? _pixelOutputBindings.Take(1)
                        : _pixelOutputBindings;
                foreach (var binding in declaredPixelOutputs)
                {
                    var outputType = GetPixelOutputType(binding.Kind);
                    var outputPointer =
                        _module.TypePointer(SpirvStorageClass.Output, outputType);
                    var variable = _module.AddGlobalVariable(
                        outputPointer,
                        SpirvStorageClass.Output);
                    _module.AddName(variable, $"mrt{binding.GuestSlot}");
                    _module.AddDecoration(
                        variable,
                        SpirvDecoration.Location,
                        binding.HostLocation);
                    _pixelOutputs.Add(
                        binding.GuestSlot,
                        new SpirvPixelOutput(variable, outputType, binding.Kind));
                    _interfaces.Add(variable);
                }
            }
            else
            {
                var inputPointer =
                    _module.TypePointer(SpirvStorageClass.Input, _uvec3Type);
                _localInvocationIdInput = _module.AddGlobalVariable(
                    inputPointer,
                    SpirvStorageClass.Input);
                _module.AddDecoration(
                    _localInvocationIdInput,
                    SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.LocalInvocationId);
                _workGroupIdInput = _module.AddGlobalVariable(
                    inputPointer,
                    SpirvStorageClass.Input);
                _module.AddDecoration(
                    _workGroupIdInput,
                    SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.WorkgroupId);
                _interfaces.Add(_localInvocationIdInput);
                _interfaces.Add(_workGroupIdInput);
            }
        }

        // PA_CL_VS_OUT_CNTL, context register 0x207 (byte address 0x2881C).
        // DIFFERENTIAL: the bit assignment below is cross-checked against
        // inspiration/shadPS4/src/video_core/amdgpu/regs_primitive.h:100-121
        // (AmdGpu::VsOutputControl) and its position in the context-register
        // block (regs.h:100-107 places it one dword past PA_CL_VTE_CNTL 0x206).
        // No decrypted 4.03 module in this tree names these fields, so this is
        // when the register is not supplied the translator refuses the export.
        private const int VsOutClipDistanceEnableShift = 0;
        private const int VsOutCullDistanceEnableShift = 8;
        private const uint VsOutUsePointSize = 1u << 16;
        private const uint VsOutUseEdgeFlag = 1u << 17;
        private const uint VsOutUseRenderTargetIndex = 1u << 18;
        private const uint VsOutUseViewportIndex = 1u << 19;
        private const uint VsOutUseKillFlag = 1u << 20;
        private const uint VsOutMiscVecEnable = 1u << 21;
        private const uint VsOutCcDist0VecEnable = 1u << 22;
        private const uint VsOutCcDist1VecEnable = 1u << 23;
        private const uint VsOutUseGsCutFlag = 1u << 25;

        // Vulkan's guaranteed minimum for maxClipDistances, maxCullDistances and
        // maxCombinedClipAndCullDistances. The translator has no device limits,
        // so it holds itself to the guaranteed floor.
        private const uint MaxCombinedClipAndCullDistances = 8;

        // SPIR-V 1.5 core BuiltIn numbers that SpirvBuiltIn does not enumerate
        // (SpirvModuleBuilder.cs is owned by another change; these are the
        // spec values from the SPIR-V 1.5 BuiltIn table).
        private const uint SpirvBuiltInPointSize = 1;
        private const uint SpirvBuiltInClipDistance = 3;
        private const uint SpirvBuiltInCullDistance = 4;
        private const uint SpirvBuiltInLayer = 9;
        private const uint SpirvBuiltInViewportIndex = 10;

        // SPIR-V 1.5 core Capability numbers, same reason.
        private const SpirvCapability CapabilityClipDistance = (SpirvCapability)32;
        private const SpirvCapability CapabilityCullDistance = (SpirvCapability)33;
        private const SpirvCapability CapabilityShaderLayer = (SpirvCapability)69;
        private const SpirvCapability CapabilityShaderViewportIndex = (SpirvCapability)70;

        /// <summary>
        /// Splits PA_CL_VS_OUT_CNTL into the position vectors the hardware
        /// packs after POS0, in the order the shader compiler emits them: the
        /// misc vector first, then clip/cull distances 0-3, then 4-7. Only the
        /// enabled vectors occupy export slots, so the Nth enabled vector is
        /// EXP target 13+N regardless of which vectors are disabled.
        /// </summary>
        private static List<PositionOutputComponent[]> DecodePositionSlots(uint control)
        {
            var slots = new List<PositionOutputComponent[]>(3);
            if ((control & VsOutMiscVecEnable) != 0)
            {
                slots.Add(
                [
                    (control & VsOutUsePointSize) != 0
                        ? new PositionOutputComponent(PositionOutputKind.PointSize, 0)
                        : PositionOutputComponent.None,
                    (control & VsOutUseEdgeFlag) != 0
                        ? new PositionOutputComponent(PositionOutputKind.EdgeFlag, 0)
                        : (control & VsOutUseGsCutFlag) != 0
                            ? new PositionOutputComponent(PositionOutputKind.GsCutFlag, 0)
                            : PositionOutputComponent.None,
                    (control & VsOutUseKillFlag) != 0
                        ? new PositionOutputComponent(PositionOutputKind.KillFlag, 0)
                        : (control & VsOutUseRenderTargetIndex) != 0
                            ? new PositionOutputComponent(PositionOutputKind.Layer, 0)
                            : PositionOutputComponent.None,
                    (control & VsOutUseViewportIndex) != 0
                        ? new PositionOutputComponent(PositionOutputKind.ViewportIndex, 0)
                        : PositionOutputComponent.None,
                ]);
            }

            if ((control & VsOutCcDist0VecEnable) != 0)
            {
                slots.Add(DecodeClipCullVector(control, 0));
            }

            if ((control & VsOutCcDist1VecEnable) != 0)
            {
                slots.Add(DecodeClipCullVector(control, 4));
            }

            return slots;
        }

        private static PositionOutputComponent[] DecodeClipCullVector(
            uint control,
            uint firstIndex)
        {
            // Hardware has eight combined clip/cull slots and a slot is either clip or cull,
            // never both (clip wins below, matching the enable-bit priority). SPIR-V instead
            // wants two INDEPENDENT arrays, each indexed from zero. So the array position of a
            // slot is its RANK AMONG SLOTS OF ITS OWN KIND, not its absolute slot number.
            //
            // Using the absolute index for both sizing and indexing sized CullDistance to
            // (highest cull slot + 1). With clip on slots 0-3 and cull on slots 4-7 -- exactly
            // eight distances, the maximum the hardware supports and precisely what
            // maxCombinedClipAndCullDistances=8 is meant to allow -- that gave clip 4 and cull
            // 8, so the combined-limit check below refused the maximum legal configuration.
            var clipMask = (control >> VsOutClipDistanceEnableShift) & 0xFFu;
            var cullMask = (control >> VsOutCullDistanceEnableShift) & 0xFFu;

            var components = new PositionOutputComponent[4];
            for (var component = 0u; component < 4; component++)
            {
                var index = firstIndex + component;
                var slotBit = 1u << (int)index;
                var lowerSlots = slotBit - 1u;

                components[component] = (clipMask & slotBit) != 0
                    ? new PositionOutputComponent(
                        PositionOutputKind.ClipDistance,
                        (uint)BitOperations.PopCount(clipMask & lowerSlots))
                    : (cullMask & slotBit) != 0
                        ? new PositionOutputComponent(
                            PositionOutputKind.CullDistance,
                            (uint)BitOperations.PopCount(cullMask & lowerSlots))
                        : PositionOutputComponent.None;
            }

            return components;
        }

        /// <summary>
        /// Declares the SPIR-V builtins backing every EXP POS1..POS3 the program
        /// actually performs, and fails the compile with a specific message for
        /// anything that cannot be mapped faithfully. Before this existed those
        /// exports were dropped on the floor with no diagnostic, so user clip
        /// planes, point size and layered rendering (cubemap faces,
        /// shadow-cascade array slices, VR multiview) silently did nothing.
        /// The historical drop survives only behind
        /// PROSPERISMO_SPIRV_ALLOW_DROPPED_POSITION_EXPORTS, and even then it is
        /// announced once per shader.
        /// </summary>
        private void DeclareVertexPositionAuxiliaryOutputs()
        {
            var exportedTargets = _state.Program.Instructions
                .Select(static instruction => instruction.Control)
                .OfType<Gen5ExportControl>()
                .Where(static export => export.Target is >= 13 and < 16)
                .Select(static export => export.Target)
                .Distinct()
                .Order()
                .ToArray();
            if (exportedTargets.Length == 0)
            {
                return;
            }

            // Plan before declaring anything: a refusal must not leave the
            // module with half its position builtins declared and never written.
            if (!TryPlanPositionOutputs(exportedTargets, out var slots, out var refusal))
            {
                var targets = string.Join(
                    ',',
                    exportedTargets.Select(static target => $"POS{target - 12}"));
                var message =
                    $"vertex position export {targets} cannot be translated: {refusal}";
                // Two different situations reach here and they deserve different answers.
                //
                // If PA_CL_VS_OUT_CNTL was never supplied we do not know whether the export
                // even matters, and nothing plumbs that register through yet -- so refusing
                // would kill EVERY draw whose vertex shader touches POS1..POS3 and trade a
                // slightly-wrong frame for no frame at all. Warn and drop instead: the
                // diagnostic already achieves the visibility, and the draw survives.
                //
                // If the register WAS supplied, the export's meaning is known and we simply
                // cannot express it (EdgeFlag has no SPIR-V builtin, the host lacks the
                // feature, the combined clip+cull limit is exceeded). That is a real
                // unrepresentable frame, so it fails loudly.
                if (_vertexPositionOutputControl is null && !_refuseDroppedPositionExports)
                {
                    ReportDiagnosticOnce(
                        $"vertex-position-export:{_state.Program.Address:X16}:{refusal}",
                        $"[SPIRV][WARN] program=0x{_state.Program.Address:X16} " +
                        $"{message}; dropping it and rendering without it. Set " +
                        "PROSPERISMO_SPIRV_STRICT_POSITION_EXPORTS=1 to fail the draw instead.");
                    return;
                }

                ReportDiagnosticOnce(
                    $"vertex-position-export:{_state.Program.Address:X16}:{refusal}",
                    $"[SPIRV][ERROR] program=0x{_state.Program.Address:X16} {message}");
                throw new InvalidOperationException(message);
            }

            for (var slot = 0; slot < slots.Count; slot++)
            {
                _positionSlots[(uint)(13 + slot)] = slots[slot];
            }

            var used = exportedTargets
                .SelectMany(target => _positionSlots[target])
                .ToArray();
            foreach (var component in used)
            {
                switch (component.Kind)
                {
                    case PositionOutputKind.ClipDistance:
                        _clipDistanceCount =
                            Math.Max(_clipDistanceCount, component.Index + 1);
                        break;
                    case PositionOutputKind.CullDistance:
                        _cullDistanceCount =
                            Math.Max(_cullDistanceCount, component.Index + 1);
                        break;
                }
            }

            if (_clipDistanceCount != 0)
            {
                _module.AddCapability(CapabilityClipDistance);
                _clipDistanceOutput = DeclareFloatArrayBuiltIn(
                    _clipDistanceCount,
                    SpirvBuiltInClipDistance,
                    "clipDistance");
            }

            if (_cullDistanceCount != 0)
            {
                _module.AddCapability(CapabilityCullDistance);
                _cullDistanceOutput = DeclareFloatArrayBuiltIn(
                    _cullDistanceCount,
                    SpirvBuiltInCullDistance,
                    "cullDistance");
            }

            if (used.Any(static component => component.Kind == PositionOutputKind.PointSize))
            {
                _pointSizeOutput = _module.AddGlobalVariable(
                    _module.TypePointer(SpirvStorageClass.Output, _floatType),
                    SpirvStorageClass.Output,
                    // A point drawn with an undefined size is a rasterization
                    // hazard; 1.0 is the fixed-function default.
                    _module.ConstantFloat(_floatType, 1f));
                _module.AddDecoration(
                    _pointSizeOutput,
                    SpirvDecoration.BuiltIn,
                    SpirvBuiltInPointSize);
                _module.AddName(_pointSizeOutput, "pointSize");
                _interfaces.Add(_pointSizeOutput);
            }

            if (used.Any(static component => component.Kind == PositionOutputKind.Layer))
            {
                _module.AddCapability(CapabilityShaderLayer);
                _layerOutput = DeclareUintBuiltIn(SpirvBuiltInLayer, "layer");
            }

            if (used.Any(static component => component.Kind == PositionOutputKind.ViewportIndex))
            {
                _module.AddCapability(CapabilityShaderViewportIndex);
                _viewportIndexOutput =
                    DeclareUintBuiltIn(SpirvBuiltInViewportIndex, "viewportIndex");
            }
        }

        /// <summary>
        /// Works out what each exported position vector carries and whether all
        /// of it can be represented. Emits nothing; on failure
        /// <paramref name="refusal"/> states exactly what could not be mapped.
        /// </summary>
        private bool TryPlanPositionOutputs(
            IReadOnlyList<uint> exportedTargets,
            out List<PositionOutputComponent[]> slots,
            out string refusal)
        {
            slots = [];
            if (_vertexPositionOutputControl is not { } control)
            {
                refusal =
                    "PA_CL_VS_OUT_CNTL (context register 0x207) was not supplied " +
                    "to the translator, so the meaning of each exported dword " +
                    "(clip distance / cull distance / point size / layer / " +
                    "viewport index) is unknown";
                return false;
            }

            var decoded = DecodePositionSlots(control);
            slots = decoded;
            foreach (var target in exportedTargets)
            {
                if (target - 13 >= decoded.Count)
                {
                    refusal =
                        $"the program exports POS{target - 12} but " +
                        $"PA_CL_VS_OUT_CNTL=0x{control:X8} enables only " +
                        $"{decoded.Count} extra position vector(s)";
                    return false;
                }
            }

            var clipCount = 0u;
            var cullCount = 0u;
            foreach (var component in exportedTargets
                         .SelectMany(target => decoded[(int)(target - 13)]))
            {
                switch (component.Kind)
                {
                    case PositionOutputKind.None:
                        break;
                    case PositionOutputKind.ClipDistance:
                        if (!_hostVertexOutputFeatures.ShaderClipDistance)
                        {
                            refusal = MissingHostFeature(
                                "ClipDistance",
                                "VkPhysicalDeviceFeatures::shaderClipDistance");
                            return false;
                        }

                        clipCount = Math.Max(clipCount, component.Index + 1);
                        break;
                    case PositionOutputKind.CullDistance:
                        if (!_hostVertexOutputFeatures.ShaderCullDistance)
                        {
                            refusal = MissingHostFeature(
                                "CullDistance",
                                "VkPhysicalDeviceFeatures::shaderCullDistance");
                            return false;
                        }

                        cullCount = Math.Max(cullCount, component.Index + 1);
                        break;
                    case PositionOutputKind.PointSize:
                        if (!_hostVertexOutputFeatures.LargePoints)
                        {
                            refusal = MissingHostFeature(
                                "PointSize",
                                "VkPhysicalDeviceFeatures::largePoints");
                            return false;
                        }

                        break;
                    case PositionOutputKind.Layer:
                        if (!_hostVertexOutputFeatures.ShaderOutputLayer)
                        {
                            refusal = MissingHostFeature(
                                "Layer",
                                "VkPhysicalDeviceVulkan12Features::shaderOutputLayer");
                            return false;
                        }

                        break;
                    case PositionOutputKind.ViewportIndex:
                        if (!_hostVertexOutputFeatures.ShaderOutputViewportIndex)
                        {
                            refusal = MissingHostFeature(
                                "ViewportIndex",
                                "VkPhysicalDeviceVulkan12Features::shaderOutputViewportIndex");
                            return false;
                        }

                        break;
                    default:
                        // EdgeFlag, KillFlag and GsCutFlag have no SPIR-V
                        // builtin and no Vulkan pipeline state to route them to.
                        // Continuing would silently discard per-vertex culling.
                        refusal =
                            $"{component.Kind} has no SPIR-V equivalent " +
                            $"(PA_CL_VS_OUT_CNTL=0x{control:X8})";
                        return false;
                }
            }

            // Hardware packs clip and cull into the same eight slots, but SPIR-V
            // needs two arrays each sized to its own highest index, and a
            // scattered enable pattern can push their combined length past what
            // Vulkan guarantees. Refuse here with the real numbers rather than
            // letting vkCreateGraphicsPipelines fail with a limit name.
            if (clipCount + cullCount > MaxCombinedClipAndCullDistances)
            {
                refusal =
                    $"ClipDistance[{clipCount}] plus CullDistance[{cullCount}] " +
                    $"exceeds the Vulkan-guaranteed " +
                    $"maxCombinedClipAndCullDistances of " +
                    $"{MaxCombinedClipAndCullDistances} " +
                    $"(PA_CL_VS_OUT_CNTL=0x{control:X8})";
                return false;
            }

            refusal = string.Empty;
            return true;
        }

        private static string MissingHostFeature(string builtIn, string vulkanFeature) =>
            $"the {builtIn} builtin needs {vulkanFeature}, which the host device " +
            "has not enabled (pass Gen5HostVertexOutputFeatures once the " +
            "presenter enables it)";

        private uint DeclareFloatArrayBuiltIn(uint count, uint builtIn, string name)
        {
            var arrayType = _module.TypeArray(_floatType, count);
            var variable = _module.AddGlobalVariable(
                _module.TypePointer(SpirvStorageClass.Output, arrayType),
                SpirvStorageClass.Output,
                // Zero keeps a vertex unclipped and unculled (both tests are
                // "distance < 0"), so array slots the program never writes -
                // the gaps left by disabled enable bits - stay harmless.
                _module.ConstantNull(arrayType));
            _module.AddDecoration(variable, SpirvDecoration.BuiltIn, builtIn);
            _module.AddName(variable, name);
            _interfaces.Add(variable);
            return variable;
        }

        private uint DeclareUintBuiltIn(uint builtIn, string name)
        {
            var variable = _module.AddGlobalVariable(
                _module.TypePointer(SpirvStorageClass.Output, _uintType),
                SpirvStorageClass.Output,
                _module.Constant(_uintType, 0));
            _module.AddDecoration(variable, SpirvDecoration.BuiltIn, builtIn);
            _module.AddName(variable, name);
            _interfaces.Add(variable);
            return variable;
        }

        /// <summary>
        /// The translator models one RDNA wave32 per 32 host lanes. A wave64
        /// host subgroup is partitioned into two independent guest waves for
        /// ballots, lane ids, EXEC masks and shuffle-style operations. Smaller
        /// Intel SIMD 8/16 subgroups cannot contain a complete guest wave and
        /// therefore still require a diagnostic.
        /// </summary>
        private void ReportHostSubgroupSizeMismatch()
        {
            if (_hostSubgroupSize == RdnaWaveLaneCount)
            {
                return;
            }

            // Ballots, EXEC predicates and shuffle-style operations partition
            // a 64-lane host subgroup into two independent guest wave32s. A
            // literal lane broadcast still names the host subgroup directly
            // and remains outside that proof.
            if (_waveLaneCount == 32 && _hostSubgroupSize == 64 &&
                !_state.Program.Instructions.Any(static instruction =>
                    instruction.Opcode is "VReadlaneB32" or "VReadfirstlaneB32"))
            {
                return;
            }

            ReportDiagnosticOnce(
                $"host-subgroup-size:{_hostSubgroupSize}:{_stage}",
                $"[SPIRV][WARN] host subgroup size is {_hostSubgroupSize} but the " +
                $"translator models a {RdnaWaveLaneCount}-lane RDNA wave; " +
                $"cross-lane results in {_stage} shaders (ballot, readlane, " +
                "mbcnt, EXEC masks) will be wrong. Pin the pipeline subgroup " +
                "size to 32 with VK_EXT_subgroup_size_control.");
        }

        private void DeclareVertexInputs()
        {
            foreach (var input in _evaluation.VertexInputs ?? [])
            {
                var componentKind = input.NumberFormat switch
                {
                    4 => VertexInputComponentKind.Uint,
                    5 => VertexInputComponentKind.Sint,
                    _ => VertexInputComponentKind.Float,
                };
                var componentType = componentKind switch
                {
                    VertexInputComponentKind.Uint => _uintType,
                    VertexInputComponentKind.Sint => _intType,
                    _ => _floatType,
                };
                var type = input.ComponentCount switch
                {
                    1u => componentType,
                    >= 2u and <= 4u =>
                        _module.TypeVector(componentType, input.ComponentCount),
                    _ => 0u,
                };
                if (type == 0)
                {
                    continue;
                }

                var pointer = _module.TypePointer(SpirvStorageClass.Input, type);
                var variable = _module.AddGlobalVariable(
                    pointer,
                    SpirvStorageClass.Input);
                _module.AddName(variable, $"attr{input.Location}");
                _module.AddDecoration(
                    variable,
                    SpirvDecoration.Location,
                    input.Location);
                _vertexInputsByPc.TryAdd(
                    input.Pc,
                    new SpirvVertexInput(
                        variable,
                        type,
                        componentType,
                        input.ComponentCount,
                        componentKind));
                _interfaces.Add(variable);
            }
        }

        private void EmitInitialState()
        {
            if (_initialScalarBufferIndex >= 0)
            {
                // Initial scalar registers arrive in a per-draw buffer instead
                // of being baked as constants, so animated user data (colors,
                // scroll offsets) reuses one translation and pipeline. Only
                // registers the program can observe need loading.
                var consumed = Gen5ShaderTranslator.ComputeConsumedScalarMask(_state.Program);
                for (uint index = 0;
                     index < _evaluation.InitialScalarRegisters.Count &&
                     index < ScalarRegisterCount;
                     index++)
                {
                    if (Gen5ShaderTranslator.IsScalarConsumed(consumed, index))
                    {
                        StoreS(
                            index,
                            LoadBufferWord(_initialScalarBufferIndex, UInt(index)));
                    }
                }

                var runtimeBufferBiasCount =
                    _globalBufferBase + _evaluation.GlobalMemoryBindings.Count;
                for (var binding = 0;
                     binding < runtimeBufferBiasCount;
                     binding++)
                {
                    Store(
                        RuntimeBufferBiasPointer(binding),
                        LoadBufferWord(
                            _initialScalarBufferIndex,
                            UInt(checked(256u + (uint)binding))));
                }
            }
            else
            {
                for (uint index = 0;
                     index < _evaluation.InitialScalarRegisters.Count &&
                     index < ScalarRegisterCount;
                     index++)
                {
                    var value = _evaluation.InitialScalarRegisters[(int)index];
                    if (value != 0)
                    {
                        StoreS(index, UInt(value));
                    }
                }
            }

            Store(_scc, _module.ConstantBool(false));
            Store(_reachedPixelExport, _module.ConstantBool(false));
            if (_subgroupInvocationIdInput != 0)
            {
                StoreWaveMask(106, _module.ConstantBool(false));
                StoreWaveMask(126, _module.ConstantBool(true));
            }
            else
            {
                // Graphics stages emulate one logical wave lane. Keep the
                // guest-visible VCC/EXEC scalar pairs synchronized with the
                // internal booleans: shaders commonly save EXEC from s126:s127
                // and restore it after divergent work. Initializing only _exec
                // left those registers at zero, so the first restore disabled
                // every fragment before its color export.
                StoreWaveMask(106, _module.ConstantBool(false));
                StoreWaveMask(126, _module.ConstantBool(true));
            }
            Store(_programCounter, UInt(0));
            Store(_programActive, _module.ConstantBool(true));

            if (_stage == Gen5SpirvStage.Vertex)
            {
                StoreV(5, Load(_uintType, _vertexIndexInput), guardWithExec: false);
                StoreV(8, Load(_uintType, _instanceIndexInput), guardWithExec: false);

                if (_domainSeeding is { } domain)
                {
                    // A domain stage receives its coordinates from the
                    // tessellator, not as a vertex index. The tessellator is
                    // fixed-function hardware, so the host stands in for it, and
                    // fw_flow_h's VGT_TF_PARAM says exactly what to produce:
                    // TESS_QUAD, PART_INTEGER, OUTPUT_TRIANGLE_CW, with every
                    // factor 12.0. That is a uniform Segments x Segments grid of
                    // quads, each split into two clockwise triangles, so the
                    // draw is Segments * Segments * 6 vertices and the index
                    // walks quad-major, corner-minor.
                    var index = Load(_uintType, _vertexIndexInput);
                    var six = UInt(6);
                    var quad = _module.AddInstruction(
                        SpirvOp.UDiv, _uintType, index, six);
                    var corner = _module.AddInstruction(
                        SpirvOp.UMod, _uintType, index, six);
                    var segments = UInt(domain.Segments);
                    var quadX = _module.AddInstruction(
                        SpirvOp.UMod, _uintType, quad, segments);
                    var quadY = _module.AddInstruction(
                        SpirvOp.UDiv, _uintType, quad, segments);

                    // Corner offsets for the two clockwise triangles of a quad:
                    // (0,0) (1,0) (1,1) and (0,0) (1,1) (0,1).
                    uint SelectOffset(uint[] table)
                    {
                        var result = UInt(table[5]);
                        for (var k = 4; k >= 0; k--)
                        {
                            var isK = _module.AddInstruction(
                                SpirvOp.IEqual, _boolType, corner, UInt((uint)k));
                            result = _module.AddInstruction(
                                SpirvOp.Select, _uintType, isK, UInt(table[k]), result);
                        }

                        return result;
                    }

                    var column = IAdd(quadX, SelectOffset([0, 1, 1, 0, 1, 0]));
                    var row = IAdd(quadY, SelectOffset([0, 0, 1, 0, 1, 1]));
                    var scale = Float(1f / domain.Segments);
                    StoreV(
                        domain.UVgpr,
                        Bitcast(
                            _uintType,
                            _module.AddInstruction(
                                SpirvOp.FMul,
                                _floatType,
                                _module.AddInstruction(SpirvOp.ConvertUToF, _floatType, column),
                                scale)),
                        guardWithExec: false);
                    StoreV(
                        domain.VVgpr,
                        Bitcast(
                            _uintType,
                            _module.AddInstruction(
                                SpirvOp.FMul,
                                _floatType,
                                _module.AddInstruction(SpirvOp.ConvertUToF, _floatType, row),
                                scale)),
                        guardWithExec: false);
                    if (domain.OffchipBytesPerPatch > 0)
                    {
                        StoreS(
                            domain.OffchipOffsetSgpr,
                            _module.AddInstruction(
                                SpirvOp.IMul,
                                _uintType,
                                Load(_uintType, _instanceIndexInput),
                                UInt(domain.OffchipBytesPerPatch)));
                    }

                    StoreV(
                        domain.PatchVgpr,
                        domain.PatchIdFromInstance
                            ? Load(_uintType, _instanceIndexInput)
                            : UInt(domain.PatchId),
                        guardWithExec: false);
                }

                // Give every declared param output a defined starting value.
                // Outputs the program actually exports overwrite this; the
                // extras that only exist to satisfy the fragment interface stay
                // zero. The explicit store also keeps SPIRV-Cross from pruning
                // an unexported output (which would re-break the interface).
                foreach (var output in _vertexOutputs.Values)
                {
                    Store(output, _module.ConstantNull(_vec4Type));
                }
            }
            else if (_stage == Gen5SpirvStage.Pixel)
            {
                var fragCoord = Load(_vec4Type, _fragCoordInput);
                EmitPixelInputState(fragCoord);
                foreach (var output in _pixelOutputs.Values)
                {
                    Store(output.Variable, _module.ConstantNull(output.Type));
                }
            }
            else
            {
                var localId = Load(_uvec3Type, _localInvocationIdInput);
                var workGroupId = Load(_uvec3Type, _workGroupIdInput);
                var invocationInBounds = _module.ConstantBool(true);
                for (uint component = 0; component < 3; component++)
                {
                    var localComponent = _module.AddInstruction(
                        SpirvOp.CompositeExtract,
                        _uintType,
                        localId,
                        component);
                    StoreV(component, localComponent, guardWithExec: false);

                    var groupComponent = _module.AddInstruction(
                        SpirvOp.CompositeExtract,
                        _uintType,
                        workGroupId,
                        component);
                    var localSize = component switch
                    {
                        0 => _localSizeX,
                        1 => _localSizeY,
                        _ => _localSizeZ,
                    };
                    var globalComponent = IAdd(
                        _module.AddInstruction(
                            SpirvOp.IMul,
                            _uintType,
                            groupComponent,
                            UInt(localSize)),
                        localComponent);
                    var limitPointer = _module.AddInstruction(
                        SpirvOp.AccessChain,
                        _pushConstantUintPointer,
                        _computeDispatchLimit,
                        UInt(0),
                        UInt(component));
                    var componentInBounds = _module.AddInstruction(
                        SpirvOp.ULessThan,
                        _boolType,
                        globalComponent,
                        Load(_uintType, limitPointer));
                    invocationInBounds = _module.AddInstruction(
                        SpirvOp.LogicalAnd,
                        _boolType,
                        invocationInBounds,
                        componentInBounds);
                }

                Store(_programActive, invocationInBounds);

                if (_state.ComputeSystemRegisters is { } registers)
                {
                    StoreComputeSystemRegister(
                        registers.WorkGroupXRegister,
                        workGroupId,
                        0);
                    StoreComputeSystemRegister(
                        registers.WorkGroupYRegister,
                        workGroupId,
                        1);
                    StoreComputeSystemRegister(
                        registers.WorkGroupZRegister,
                        workGroupId,
                        2);
                    if (registers.ThreadGroupSizeRegister is { } sizeRegister)
                    {
                        StoreS(
                            sizeRegister,
                            UInt(checked(_localSizeX * _localSizeY * _localSizeZ)));
                    }
                }

                if (_mergedWaveSeeding is { OffchipBytesPerGroup: > 0 } offchip)
                {
                    // The offchip patch ring and the tessellation factor buffer
                    // are addressed from a per-threadgroup byte offset the SPI
                    // supplies in an SGPR, which is why the shader's own address
                    // arithmetic only carries a within-group index.
                    var group = _module.AddInstruction(
                        SpirvOp.CompositeExtract,
                        _uintType,
                        Load(_uvec3Type, _workGroupIdInput),
                        0u);
                    StoreS(
                        offchip.OffchipOffsetSgpr,
                        _module.AddInstruction(
                            SpirvOp.IMul, _uintType, group, UInt(offchip.OffchipBytesPerGroup)));
                    if (offchip.FactorBytesPerGroup > 0)
                    {
                        StoreS(
                            offchip.FactorOffsetSgpr,
                            _module.AddInstruction(
                                SpirvOp.IMul, _uintType, group, UInt(offchip.FactorBytesPerGroup)));
                    }
                }

                if (_mergedWaveSeeding is { } seeding)
                {
                    // A merged local+hull wave does not receive the compute
                    // VGPR contract. Reproduce the SPI's distribution: the flat
                    // invocation id as the vertex index, the LDS slot the local
                    // section writes through, and the packed patch/control-point
                    // id the hull section unpacks.
                    var flat = LoadV(0);
                    var stride = UInt(seeding.LdsSlotStride);

                    // Two different patch indices are in play and conflating
                    // them double-counts the offset. The packed id the hull
                    // unpacks addresses the offchip ring *within* this
                    // threadgroup - the group's base arrives separately in an
                    // SGPR - while the vertex index the local section fetches
                    // through is the patch's position in the whole draw.
                    uint control, withinGroup;
                    if (seeding.PatchesPerGroup > 1)
                    {
                        control = _module.AddInstruction(
                            SpirvOp.UMod, _uintType, flat, stride);
                        withinGroup = _module.AddInstruction(
                            SpirvOp.UDiv, _uintType, flat, stride);
                    }
                    else
                    {
                        control = flat;
                        withinGroup = UInt(0);
                    }

                    var globalPatch = withinGroup;
                    if (seeding.PatchIdFromWorkgroup)
                    {
                        var group = _module.AddInstruction(
                            SpirvOp.CompositeExtract,
                            _uintType,
                            Load(_uvec3Type, _workGroupIdInput),
                            0u);
                        globalPatch = IAdd(
                            withinGroup,
                            _module.AddInstruction(
                                SpirvOp.IMul,
                                _uintType,
                                group,
                                UInt(seeding.PatchesPerGroup)));
                    }
                    else if (seeding.PatchId != 0)
                    {
                        globalPatch = IAdd(withinGroup, UInt(seeding.PatchId));
                    }

                    uint vertexIndex;
                    if (seeding.LatticeRowLength > 0)
                    {
                        var width = UInt(seeding.LatticeRowLength);
                        var four = UInt(4);

                        // The wrapping direction contributes LatticeWrapSpans
                        // patch columns and its index closes on itself; the
                        // clamped direction contributes width - 3.
                        var spans = seeding.LatticeWrapSpans > 0
                            ? seeding.LatticeWrapSpans
                            : seeding.LatticeRowLength - 3;
                        var patchCols = UInt(spans);

                        var column = IAdd(
                            _module.AddInstruction(
                                SpirvOp.UMod, _uintType, globalPatch, patchCols),
                            _module.AddInstruction(SpirvOp.UMod, _uintType, control, four));
                        if (seeding.LatticeWrapSpans > 0)
                        {
                            column = _module.AddInstruction(
                                SpirvOp.UMod, _uintType, column, width);
                        }

                        var row = IAdd(
                            _module.AddInstruction(
                                SpirvOp.UDiv, _uintType, globalPatch, patchCols),
                            _module.AddInstruction(SpirvOp.UDiv, _uintType, control, four));

                        vertexIndex = IAdd(
                            _module.AddInstruction(SpirvOp.IMul, _uintType, row, width),
                            column);
                    }
                    else
                    {
                        vertexIndex = IAdd(
                            control,
                            _module.AddInstruction(
                                SpirvOp.IMul, _uintType, globalPatch, stride));
                    }

                    StoreV(seeding.VertexIndexVgpr, vertexIndex, guardWithExec: false);
                    StoreV(seeding.LdsSlotVgpr, flat, guardWithExec: false);
                    StoreV(
                        seeding.PackedIdVgpr,
                        BitwiseOr(ShiftLeftLogical(control, UInt(8)), withinGroup),
                        guardWithExec: false);
                }
            }
        }

        private void EmitPixelInputState(uint fragCoord)
        {
            uint vgpr = 0;

            // Pixel input VGPRs are compacted in SPI_PS_INPUT_ADDR order. The
            // interpolation inputs occupy register slots even though V_INTERP
            // is lowered directly from SPIR-V interpolants; position inputs
            // following them must still land in the hardware-selected VGPRs.
            AdvancePixelInput(0, 2, ref vgpr); // PERSP_SAMPLE
            AdvancePixelInput(1, 2, ref vgpr); // PERSP_CENTER
            AdvancePixelInput(2, 2, ref vgpr); // PERSP_CENTROID
            AdvancePixelInput(3, 3, ref vgpr); // PERSP_PULL_MODEL
            AdvancePixelInput(4, 2, ref vgpr); // LINEAR_SAMPLE
            AdvancePixelInput(5, 2, ref vgpr); // LINEAR_CENTER
            AdvancePixelInput(6, 2, ref vgpr); // LINEAR_CENTROID
            AdvancePixelInput(7, 1, ref vgpr); // LINE_STIPPLE

            EmitPixelPositionInput(8, 0, fragCoord, ref vgpr); // POS_X_FLOAT
            EmitPixelPositionInput(9, 1, fragCoord, ref vgpr); // POS_Y_FLOAT
            EmitPixelPositionInput(10, 2, fragCoord, ref vgpr); // POS_Z_FLOAT
            EmitPixelPositionInput(11, 3, fragCoord, ref vgpr); // POS_W_FLOAT

            // FRONT_FACE, ANCILLARY, SAMPLE_COVERAGE and POS_FIXED_PT follow
            // position inputs. Reserve their compact slots until their SPIR-V
            // builtins are needed by a guest shader.
            AdvancePixelInput(12, 1, ref vgpr);
            AdvancePixelInput(13, 1, ref vgpr);
            AdvancePixelInput(14, 1, ref vgpr);
            AdvancePixelInput(15, 1, ref vgpr);
        }

        private void AdvancePixelInput(int bit, uint dwordCount, ref uint vgpr)
        {
            if ((_pixelInputAddress & (1u << bit)) != 0)
            {
                vgpr += dwordCount;
            }
        }

        private void EmitPixelPositionInput(
            int bit,
            uint component,
            uint fragCoord,
            ref uint vgpr)
        {
            var mask = 1u << bit;
            if ((_pixelInputAddress & mask) == 0)
            {
                return;
            }

            if ((_pixelInputEnable & mask) != 0)
            {
                var value = _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    _floatType,
                    fragCoord,
                    component);
                StoreV(vgpr, Bitcast(_uintType, value), guardWithExec: false);
            }

            vgpr++;
        }

        private void StoreComputeSystemRegister(
            uint? register,
            uint workGroupId,
            uint component)
        {
            if (register is null)
            {
                return;
            }

            var value = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _uintType,
                workGroupId,
                component);
            StoreS(register.Value, value);
        }

        private bool TryEmitBlock(
            IReadOnlyList<ShaderBlock> blocks,
            int blockIndex,
            out string error)
        {
            error = string.Empty;
            var block = blocks[blockIndex];
            for (var index = block.StartIndex; index < block.EndIndex; index++)
            {
                var instruction = _state.Program.Instructions[index];
                if (IsBranch(instruction.Opcode) || instruction.Opcode == "SEndpgm")
                {
                    continue;
                }

                if (!TryEmitInstruction(instruction, out error))
                {
                    error = $"pc=0x{instruction.Pc:X} {instruction.Opcode}: {error}";
                    return false;
                }

                CapturePixelVgprs(instruction);
                CapturePixelVgprPoints(instruction);
                MarkPixelPath(instruction);
                CapturePixelExec(instruction);
            }

            var terminator = _state.Program.Instructions[block.EndIndex - 1];
            if (terminator.Opcode == "SEndpgm")
            {
                Store(_programActive, _module.ConstantBool(false));
                return true;
            }

            var fallthrough = blockIndex + 1 < blocks.Count
                ? (uint)(blockIndex + 1)
                : uint.MaxValue;
            if (terminator.Opcode == "SBranch")
            {
                if (!TryGetBranchTargetPc(terminator, out var targetPc))
                {
                    error = "invalid scalar branch target";
                    return false;
                }

                if (IsExitBranchTarget(_state.Program.Instructions, targetPc))
                {
                    Store(_programActive, _module.ConstantBool(false));
                    return true;
                }

                if (!TryFindBlock(blocks, targetPc, out var targetBlock))
                {
                    error = $"invalid scalar branch target pc=0x{terminator.Pc:X} target=0x{targetPc:X} blocks={FormatBlockStarts(blocks)}";
                    return false;
                }

                Store(_programCounter, UInt((uint)targetBlock));
                return true;
            }

            if (terminator.Opcode.StartsWith("SCbranch", StringComparison.Ordinal))
            {
                var hasTarget = TryGetBranchTargetPc(terminator, out var targetPc);
                var targetBlock = -1;
                var hasTargetBlock = hasTarget && TryFindBlock(blocks, targetPc, out targetBlock);
                var targetExits = hasTarget && IsExitBranchTarget(_state.Program.Instructions, targetPc);
                var hasCondition = TryGetBranchCondition(terminator.Opcode, out var condition);
                if (!hasTarget || (!hasTargetBlock && !targetExits) || !hasCondition)
                {
                    error =
                        $"invalid conditional scalar branch opcode={terminator.Opcode} " +
                        $"pc=0x{terminator.Pc:X} " +
                        $"target={(hasTarget ? $"0x{targetPc:X}" : "invalid")} " +
                        $"target_block={(hasTargetBlock ? targetBlock.ToString() : targetExits ? "exit" : "missing")} " +
                        $"fallthrough={(fallthrough == uint.MaxValue ? "end" : fallthrough.ToString())} " +
                        $"condition={hasCondition} " +
                        $"blocks={FormatBlockStarts(blocks)}";
                    return false;
                }

                var takenBlock = targetExits ? uint.MaxValue : (uint)targetBlock;
                var selected = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    condition,
                    UInt(takenBlock),
                    UInt(fallthrough));
                Store(_programCounter, selected);
                return true;
            }

            if (fallthrough == uint.MaxValue)
            {
                Store(_programActive, _module.ConstantBool(false));
            }
            else
            {
                Store(_programCounter, UInt(fallthrough));
            }

            return true;
        }

        private static string FormatBlockStarts(IReadOnlyList<ShaderBlock> blocks)
        {
            const int maxBlocks = 32;
            var count = Math.Min(blocks.Count, maxBlocks);
            var starts = new string[count];
            for (var index = 0; index < count; index++)
            {
                starts[index] = $"0x{blocks[index].StartPc:X}";
            }

            return blocks.Count <= maxBlocks
                ? string.Join(",", starts)
                : string.Join(",", starts) + $",...({blocks.Count})";
        }

        private static bool IsExitBranchTarget(
            IReadOnlyList<Gen5ShaderInstruction> instructions,
            uint targetPc)
        {
            if (instructions.Count == 0)
            {
                return false;
            }

            var last = instructions[^1];
            var lastEndPc = last.Pc + (uint)(last.Words.Count * sizeof(uint));
            return targetPc >= lastEndPc;
        }

        private bool TryGetBranchCondition(string opcode, out uint condition)
        {
            condition = opcode switch
            {
                "SCbranchScc0" => LogicalNot(Load(_boolType, _scc)),
                "SCbranchScc1" => Load(_boolType, _scc),
                "SCbranchVccz" => LogicalNot(SubgroupAny(Load(_boolType, _vcc))),
                "SCbranchVccnz" => SubgroupAny(Load(_boolType, _vcc)),
                "SCbranchExecz" => LogicalNot(SubgroupAny(Load(_boolType, _exec))),
                "SCbranchExecnz" => SubgroupAny(Load(_boolType, _exec)),
                // The conditional-debug branches test MODE.cond_dbg_sys /
                // cond_dbg_user, which only a debugger sets; on retail hardware
                // they always fall through. The decoder already documents this
                // (Gen5ShaderTranslator.cs, DecodeSopp 0x17-0x1A) but the
                // structurizer had no arm for them, so a shader containing one
                // failed with "invalid conditional scalar branch" and the draw
                // was dropped. A constant-false condition is the retail path,
                // and it keeps the branch structurally intact rather than
                // deleting an edge the block layout still expects.
                "SCbranchCdbgsys" or
                "SCbranchCdbguser" or
                "SCbranchCdbgsysOrUser" or
                "SCbranchCdbgsysAndUser" => _module.ConstantBool(false),
                _ => 0,
            };
            return condition != 0;
        }

        private bool TryEmitInstruction(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            if (instruction.Opcode is
                "SNop" or
                "SWaitcnt" or
                // GFX10 splits s_waitcnt into per-counter SOPK forms. LLVM emits
                // s_waitcnt_vscnt routinely for store ordering, and unlike the
                // SOPP original these reach TryEmitScalarAlu, which rejects them
                // and fails the whole shader. They are scheduling hints against
                // hardware memory counters we do not model; SPIR-V already
                // orders an invocation's own accesses and s_barrier carries the
                // cross-invocation ordering.
                "SWaitcntVscnt" or
                "SWaitcntVmcnt" or
                "SWaitcntExpcnt" or
                "SWaitcntLgkmcnt" or
                "SInstPrefetch" or
                "STtraceData" or
                // NGG shaders bracket their exports with s_sendmsg
                // (GS_ALLOC_REQ/DEALLOC) to reserve hardware export space;
                // exports are translated directly, so the message is moot.
                "SSendmsg")
            {
                return true;
            }

            if (instruction.Opcode == "SBarrier")
            {
                var workgroup = UInt(2);
                var semantics = UInt(WorkgroupBarrierMemorySemantics);
                _module.AddStatement(
                    SpirvOp.ControlBarrier,
                    workgroup,
                    workgroup,
                    semantics);
                return true;
            }

            if (instruction.Control is Gen5ScalarMemoryControl scalarMemory)
            {
                return TryEmitScalarMemory(instruction, scalarMemory, out error);
            }

            if (instruction.Control is Gen5InterpolationControl interpolation)
            {
                return TryEmitInterpolation(instruction, interpolation, out error);
            }

            if (instruction.Control is Gen5ImageControl image)
            {
                return TryEmitImage(instruction, image, out error);
            }

            if (instruction.Control is Gen5GlobalMemoryControl globalMemory)
            {
                return TryEmitGlobalMemory(instruction, globalMemory, out error);
            }

            if (instruction.Control is Gen5BufferMemoryControl bufferMemory)
            {
                return TryEmitBufferMemory(instruction, bufferMemory, out error);
            }

            if (instruction.Control is Gen5ExportControl export)
            {
                return TryEmitExport(instruction, export, out error);
            }

            if (instruction.Control is Gen5DataShareControl)
            {
                return TryEmitDataShare(instruction, out error);
            }

            if (instruction.Encoding is
                Gen5ShaderEncoding.Sop1 or
                Gen5ShaderEncoding.Sop2 or
                Gen5ShaderEncoding.Sopc or
                Gen5ShaderEncoding.Sopk)
            {
                return TryEmitScalarAlu(instruction, out error);
            }

            if (instruction.Encoding is Gen5ShaderEncoding.Sopp)
            {
                return TryEmitScalarProgramControl(instruction, out error);
            }

            if (instruction.Encoding is
                Gen5ShaderEncoding.Smrd or
                Gen5ShaderEncoding.Smem)
            {
                return true;
            }

            return TryEmitVectorAlu(instruction, out error);
        }

        /// <summary>
        /// SOPP instructions that carry no data flow. Each name is classified
        /// explicitly so a decoded-but-unclassified SOPP fails loudly instead of
        /// being swallowed by a blanket accept.
        /// </summary>
        private bool TryEmitScalarProgramControl(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            switch (instruction.Opcode)
            {
                // Genuine no-ops for this model. Every one of these acts only on
                // hardware state the translator does not have: wave scheduling
                // priority and sleep, instruction-cache and performance-counter
                // control, shader-processor-input clause hints, hazard-counter
                // hints, thread-trace payloads, and end-of-code padding.
                case "SWakeup":
                case "SSleep":
                case "SSetprio":
                case "SIcacheInv":
                case "SIncperflevel":
                case "SDecperflevel":
                case "SCodeEnd":
                case "SClause":
                case "SWaitIdle":
                case "SWaitcntDepctr":
                case "STtracedataImm":
                    return true;

                // Debug/abort controls. On retail hardware these fire only for an
                // attached debugger or a driver-installed trap handler; a shader
                // that reaches one has already left the renderable path. Treating
                // them as no-ops keeps the draw, so record that the divergence
                // happened rather than hiding it.
                case "SSethalt":
                case "SSetkill":
                case "STrap":
                case "SSendmsghalt":
                    ReportDiagnosticOnce(
                        $"sopp-debug-control:{instruction.Opcode}",
                        $"[SPIRV][WARN] program=0x{_state.Program.Address:X16} " +
                        $"pc=0x{instruction.Pc:X} {instruction.Opcode} ignored: " +
                        "wave halt/kill/trap and the halting sendmsg have no " +
                        "SPIR-V equivalent, so the shader continues executing " +
                        "where hardware would have stopped or trapped.");
                    return true;

                // s_round_mode / s_denorm_mode rewrite MODE.fp_round and
                // MODE.fp_denorm for the rest of the program. SPIR-V can only
                // express rounding per instruction (FPRoundingMode, and only on
                // conversions) and denormal handling per module
                // (DenormFlushToZero/DenormPreserve execution modes), neither of
                // which can be switched mid-shader. Ignoring them keeps IEEE
                // round-to-nearest-even with host denormal behaviour, which is
                // the common case a shader is switching *to*; say so loudly
                // because it is a real numeric divergence, not a no-op.
                case "SRoundMode":
                case "SDenormMode":
                    ReportDiagnosticOnce(
                        $"sopp-fp-mode:{instruction.Opcode}",
                        $"[SPIRV][WARN] program=0x{_state.Program.Address:X16} " +
                        $"pc=0x{instruction.Pc:X} {instruction.Opcode} ignored: " +
                        "SPIR-V cannot change the floating-point rounding or " +
                        "denormal mode mid-shader, so this program keeps " +
                        "round-to-nearest-even and the host denormal mode.");
                    return true;

                default:
                    // s_nop / s_waitcnt / s_sendmsg / s_endpgm / branches never
                    // reach here: they are handled earlier or by the block
                    // structurizer. Anything else is a SOPP the decoder learned
                    // about but the emitter never classified.
                    ReportDiagnosticOnce(
                        $"sopp-unclassified:{instruction.Opcode}",
                        $"[SPIRV][ERROR] program=0x{_state.Program.Address:X16} " +
                        $"pc=0x{instruction.Pc:X} {instruction.Opcode} is decoded " +
                        "but has no SPIR-V classification. Add it to " +
                        "TryEmitScalarProgramControl with a reason instead of " +
                        "letting it pass silently.");
                    error = $"unclassified scalar program control {instruction.Opcode}";
                    return false;
            }
        }

        private bool TryEmitDataShare(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            if (instruction.Control is not Gen5DataShareControl control)
            {
                error = "invalid LDS instruction";
                return false;
            }

            if (control.Gds)
            {
                error = "GDS data share is not implemented";
                return false;
            }

            // ds_permute/ds_bpermute/ds_swizzle borrow the LDS crossbar but read
            // and write no LDS memory, so they are legal in a wave that has no
            // LDS allocation at all. Handle them before the storage guard.
            if (instruction.Opcode is
                "DsPermuteB32" or "DsBpermuteB32" or "DsSwizzleB32")
            {
                return TryEmitDataShareShuffle(instruction, control, out error);
            }

            if (_lds == 0 || _ldsElementPointer == 0)
            {
                error = "invalid LDS instruction";
                return false;
            }

            switch (instruction.Opcode)
            {
                case "DsWriteB32":
                {
                    if (instruction.Sources.Count < 2)
                    {
                        error = "missing LDS write source";
                        return false;
                    }

                    var address = GetRawSource(instruction, 0);
                    StoreLds(
                        LdsPointer(address, control.Offset0),
                        GetRawSource(instruction, 1));
                    return true;
                }
                case "DsWriteB64":
                {
                    if (instruction.Sources.Count < 3)
                    {
                        error = "missing LDS write64 source";
                        return false;
                    }

                    var address = GetRawSource(instruction, 0);
                    var offset = control.Offset0;
                    StoreLds(LdsPointer(address, offset), GetRawSource(instruction, 1));
                    StoreLds(
                        LdsPointer(address, offset + sizeof(uint)),
                        GetRawSource(instruction, 2));
                    return true;
                }
                case "DsWriteB96":
                case "DsWriteB128":
                {
                    // ds_write_b96 stores 3 consecutive dwords, ds_write_b128
                    // stores 4, from data0..data0+N at the address's offset.
                    var dwordCount = instruction.Opcode == "DsWriteB128" ? 4 : 3;
                    if (instruction.Sources.Count < 1 + dwordCount)
                    {
                        error = "missing LDS write128 source";
                        return false;
                    }

                    var address = GetRawSource(instruction, 0);
                    var offset = control.Offset0;
                    for (var dword = 0; dword < dwordCount; dword++)
                    {
                        StoreLds(
                            LdsPointer(address, offset + (uint)(dword * sizeof(uint))),
                            GetRawSource(instruction, 1 + dword));
                    }

                    return true;
                }
                case "DsWrite2B32":
                case "DsWrite2St64B32":
                {
                    if (instruction.Sources.Count < 3)
                    {
                        error = "missing LDS write2 source";
                        return false;
                    }

                    var st64 = instruction.Opcode == "DsWrite2St64B32";
                    var address = GetRawSource(instruction, 0);
                    StoreLds(
                        LdsPointer(
                            address,
                            EffectiveDsPairOffsetBytes(control.Offset0, st64)),
                        GetRawSource(instruction, 1));
                    StoreLds(
                        LdsPointer(
                            address,
                            EffectiveDsPairOffsetBytes(control.Offset1, st64)),
                        GetRawSource(instruction, 2));
                    return true;
                }
                case "DsReadB32":
                {
                    if (instruction.Destinations.Count < 1 ||
                        instruction.Sources.Count < 1)
                    {
                        error = "missing LDS read operand";
                        return false;
                    }

                    var address = GetRawSource(instruction, 0);
                    var value = LoadLds(LdsPointer(address, control.Offset0));
                    StoreV(instruction.Destinations[0].Value, value);
                    return true;
                }
                case "DsReadB96":
                case "DsReadB128":
                {
                    // ds_read_b96 loads 3 consecutive dwords, ds_read_b128 loads
                    // 4, into dest..dest+N from the address's offset.
                    var dwordCount = instruction.Opcode == "DsReadB128" ? 4 : 3;
                    if (instruction.Destinations.Count < dwordCount ||
                        instruction.Sources.Count < 1)
                    {
                        error = "missing LDS read128 operand";
                        return false;
                    }

                    var address = GetRawSource(instruction, 0);
                    var offset = control.Offset0;
                    for (var dword = 0; dword < dwordCount; dword++)
                    {
                        var value = LoadLds(
                            LdsPointer(address, offset + (uint)(dword * sizeof(uint))));
                        StoreV(instruction.Destinations[dword].Value, value);
                    }

                    return true;
                }
                case "DsRead2B32":
                case "DsRead2St64B32":
                {
                    if (instruction.Destinations.Count < 2 ||
                        instruction.Sources.Count < 1)
                    {
                        error = "missing LDS read2 operand";
                        return false;
                    }

                    var st64 = instruction.Opcode == "DsRead2St64B32";
                    var address = GetRawSource(instruction, 0);
                    var first = LoadLds(
                        LdsPointer(
                            address,
                            EffectiveDsPairOffsetBytes(control.Offset0, st64)));
                    var second = LoadLds(
                        LdsPointer(
                            address,
                            EffectiveDsPairOffsetBytes(control.Offset1, st64)));
                    StoreV(instruction.Destinations[0].Value, first);
                    StoreV(instruction.Destinations[1].Value, second);
                    return true;
                }
                default:
                    if (Gen5ShaderTranslator.IsDataShareAtomic(instruction.Opcode))
                    {
                        return TryEmitDataShareAtomic(instruction, control, out error);
                    }

                    error = $"unsupported LDS opcode {instruction.Opcode}";
                    return false;
            }
        }

        /// <summary>
        /// ds_permute_b32 / ds_bpermute_b32 / ds_swizzle_b32: the LDS crossbar
        /// used purely as a cross-lane shuffle. No LDS storage is touched.
        /// </summary>
        /// <remarks>
        /// RDNA2 addresses these by BYTE, so the lane selector is ADDR >> 2 (the
        /// hardware ignores the low two bits). The selector is then taken modulo
        /// the wave width; this translator models a guest wave as
        /// <see cref="ModelledWaveLaneCount"/> host subgroup lanes, so the mask
        /// is <see cref="GuestWaveLaneMask"/> rather than a hard-coded 31.
        /// </remarks>
        private bool TryEmitDataShareShuffle(
            Gen5ShaderInstruction instruction,
            Gen5DataShareControl control,
            out string error)
        {
            error = string.Empty;
            if (!TryGetVectorDestination(instruction, out var destination))
            {
                error = $"missing vector destination for {instruction.Opcode}";
                return false;
            }

            if (_subgroupInvocationIdInput == 0)
            {
                // Without SubgroupLocalInvocationId there is exactly one logical
                // lane, so every cross-lane selector other than "myself" is
                // unrepresentable. Refuse rather than silently emit a move: a
                // shuffle that always returns the caller's own value is not a
                // conservative approximation of a shuffle, it is a different
                // program.
                ReportDiagnosticOnce(
                    $"ds-shuffle-no-subgroup:{_stage}:{instruction.Opcode}",
                    $"[SPIRV][ERROR] program=0x{_state.Program.Address:X16} " +
                    $"stage={_stage} {instruction.Opcode} needs cross-lane " +
                    "shuffles, but this stage is compiled without a subgroup " +
                    "invocation id (subgroup ops are only declared for compute). " +
                    "Translation fails instead of degrading the shuffle to a move.");
                error =
                    $"{instruction.Opcode} requires subgroup support in stage {_stage}";
                return false;
            }

            if (_waveLaneCount == 64)
            {
                // A 64-lane guest wave is modelled on a 32-lane host subgroup.
                // Two things break at once: OpGroupNonUniformShuffle cannot
                // reach the other 32 lanes, and in this mode GuestWaveLane()
                // comes from LocalInvocationIndex while the shuffle indexes by
                // SubgroupLocalInvocationId, so the selector and the lane it is
                // compared against are not even in the same numbering. That
                // does not produce a truncated shuffle, it produces a wrong
                // one. Refuse; a dropped dispatch beats a silently wrong one.
                //
                // The condition is the DECLARED wave width, not
                // _emulateWave64. GuestWaveLane() keys on _waveLaneCount == 64
                // (it reads LocalInvocationIndex, which DeclareStageInterface
                // declares under that same test), so the lane-numbering
                // mismatch this comment describes exists just as much when the
                // wave64 bridge is off - and when it is off there is not even a
                // rendezvous to fall back on. Gating on _emulateWave64 left
                // every wave64 dispatch whose workgroup is not exactly 64
                // threads emitting the wrong shuffle silently, and for
                // ds_swizzle an OpGroupNonUniformShuffle whose id can exceed
                // the host subgroup size, which SPIR-V leaves undefined.
                ReportDiagnosticOnce(
                    $"ds-shuffle-wave64:{instruction.Opcode}",
                    $"[SPIRV][ERROR] program=0x{_state.Program.Address:X16} " +
                    $"{instruction.Opcode} in a wave64 program cannot be " +
                    $"expressed on a {_hostSubgroupSize}-lane host subgroup: " +
                    "the crossbar spans 64 guest lanes and the wave64 bridge " +
                    "numbers lanes by LocalInvocationIndex, not by " +
                    "SubgroupLocalInvocationId. Translation fails rather than " +
                    "shuffling against the wrong lane numbering.");
                error =
                    $"{instruction.Opcode} cannot be modelled in a wave64 program";
                return false;
            }

            var lane = GuestWaveLane();
            if (instruction.Opcode == "DsSwizzleB32")
            {
                if (instruction.Sources.Count < 1)
                {
                    error = "missing ds_swizzle source";
                    return false;
                }

                var swizzleOffset = ((control.Offset1 & 0xFFu) << 8) |
                    (control.Offset0 & 0xFFu);
                if (!TryGetSwizzleLane(swizzleOffset, lane, out var swizzleLane))
                {
                    ReportDiagnosticOnce(
                        $"ds-swizzle-mode:0x{swizzleOffset:X4}",
                        $"[SPIRV][ERROR] program=0x{_state.Program.Address:X16} " +
                        $"ds_swizzle_b32 offset 0x{swizzleOffset:X4} selects a " +
                        "mode this translator does not implement (only " +
                        "QUAD_PERM, offset&0xFF00==0x8000, and BITMASK_PERM, " +
                        "offset&0x8000==0, are implemented; FFT and rotate " +
                        "modes are not).");
                    error =
                        $"unsupported ds_swizzle mode 0x{swizzleOffset:X4}";
                    return false;
                }

                StoreV(
                    destination,
                    SubgroupShuffle(GetRawSource(instruction, 0), swizzleLane));
                return true;
            }

            if (instruction.Sources.Count < 2)
            {
                error = $"missing {instruction.Opcode} operands (addr, data0)";
                return false;
            }

            var selector = BitwiseAnd(
                ShiftRightLogical(GetRawSource(instruction, 0), UInt(2)),
                UInt(GuestWaveLaneMask));
            var value = GetRawSource(instruction, 1);

            if (instruction.Opcode == "DsBpermuteB32")
            {
                // Backward permute (a gather): VDST[i] = DATA0[ADDR[i] >> 2].
                // This is exactly OpGroupNonUniformShuffle. StoreV applies the
                // guest EXEC guard for the destination half of the ISA rule.
                StoreV(destination, SubgroupShuffle(value, selector));
                return true;
            }

            // Forward permute (a scatter): tmp[ADDR[i] >> 2] = DATA0[i] for every
            // EXEC-enabled source lane i, then VDST[i] = tmp[i]. SPIR-V has no
            // scatter, so invert it into one gather per source lane: lane j asks
            // every lane k where k was aiming and what it was sending, and keeps
            // the match. Iterating k upwards makes the highest-numbered source
            // lane win, which is the tie-break the ISA specifies. Cost is two
            // OpGroupNonUniformShuffle per modelled wave lane.
            var inactiveSelector = UInt(0xFFFF_FFFF);
            var guardedSelector = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                Load(_boolType, _exec),
                selector,
                inactiveSelector);
            var result = LoadV(destination);
            for (var source = 0u; source < ModelledWaveLaneCount; source++)
            {
                var sourceLane = UInt(source);
                var targeted = SubgroupShuffle(guardedSelector, sourceLane);
                var sent = SubgroupShuffle(value, sourceLane);
                result = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    _module.AddInstruction(
                        SpirvOp.IEqual,
                        _boolType,
                        targeted,
                        lane),
                    sent,
                    result);
            }

            // Lanes no source aimed at keep their previous VDST value. The ISA
            // leaves them undefined (tmp[] is never initialised), so preserving
            // is a legal reading and avoids inventing zeroes.
            StoreV(destination, result);
            return true;
        }

        /// <summary>
        /// Resolves the ds_swizzle_b32 16-bit offset field into the lane each
        /// invocation reads from, or returns false for a mode we do not model.
        /// </summary>
        /// <remarks>
        /// Mode encodings follow LLVM's <c>AMDGPU::Swizzle</c> constants:
        /// QUAD_PERM is <c>(offset &amp; 0xFF00) == 0x8000</c> and BITMASK_PERM is
        /// <c>(offset &amp; 0x8000) == 0</c>. GFX9+ additionally defines FFT and
        /// rotate modes in the remaining space; KytyPS5 rejects those too
        /// (MemoryOps.cpp:437 "DS swizzle FFT mode is not implemented").
        /// The BITMASK field split is cross-checked against shadPS4
        /// frontend/translate/data_share.cpp:308-317.
        /// </remarks>
        private bool TryGetSwizzleLane(uint offset, uint lane, out uint swizzleLane)
        {
            swizzleLane = 0;
            if ((offset & 0xFF00) == 0x8000)
            {
                // QUAD_PERM: each lane of a group of four selects one of that
                // group's four lanes via a 2-bit field of offset[7:0].
                var laneInQuad = BitwiseAnd(lane, UInt(3));
                var shift = ShiftLeftLogical(laneInQuad, UInt(1));
                var selection = BitwiseAnd(
                    ShiftRightLogical(UInt(offset & 0xFF), shift),
                    UInt(3));
                swizzleLane = BitwiseAnd(
                    BitwiseOr(
                        BitwiseAnd(lane, UInt(GuestWaveLaneMask & ~3u)),
                        selection),
                    UInt(GuestWaveLaneMask));
                return true;
            }

            if ((offset & 0x8000) != 0)
            {
                return false;
            }

            // BITMASK_PERM: lane' = ((lane & and_mask) | or_mask) ^ xor_mask,
            // with and_mask = offset[4:0], or_mask = offset[9:5],
            // xor_mask = offset[14:10].
            var andMask = offset & 0x1Fu;
            var orMask = (offset >> 5) & 0x1Fu;
            var xorMask = (offset >> 10) & 0x1Fu;
            swizzleLane = BitwiseAnd(
                BitwiseXor(
                    BitwiseOr(BitwiseAnd(lane, UInt(andMask)), UInt(orMask)),
                    UInt(xorMask)),
                UInt(GuestWaveLaneMask));
            return true;
        }

        private uint SubgroupShuffle(uint value, uint lane)
        {
            // A host subgroup may contain two independent RDNA wave32s. Keep
            // a guest lane index inside the current invocation's 32-lane half
            // instead of sending lanes 32..63 back into host lanes 0..31.
            if (_hostSubgroupSize > RdnaWaveLaneCount &&
                _subgroupInvocationIdInput != 0)
            {
                var hostLane = Load(_uintType, _subgroupInvocationIdInput);
                var waveBase = BitwiseAnd(hostLane, UInt(~(RdnaWaveLaneCount - 1u)));
                lane = BitwiseOr(waveBase, BitwiseAnd(lane, UInt(RdnaWaveLaneCount - 1u)));
            }

            return _module.AddInstruction(
                SpirvOp.GroupNonUniformShuffle,
                _uintType,
                UInt(3),  // Subgroup scope
                value,
                lane);
        }

        private static uint EffectiveDsPairOffsetBytes(uint offset, bool st64 = false) =>
            offset * (st64 ? 256u : sizeof(uint));

        private readonly record struct LdsAccess(uint Pointer, uint InRange);

        private LdsAccess LdsPointer(uint address, uint offsetBytes)
        {
            var addressWithOffset = offsetBytes == 0
                ? address
                : IAdd(address, UInt(offsetBytes));
            var index = ShiftRightLogical(addressWithOffset, UInt(2));
            var inRange = _module.AddInstruction(
                SpirvOp.ULessThan,
                _boolType,
                index,
                UInt(_ldsDwordCount));
            if (offsetBytes != 0)
            {
                var didNotWrap = _module.AddInstruction(
                    SpirvOp.UGreaterThanEqual,
                    _boolType,
                    addressWithOffset,
                    address);
                inRange = _module.AddInstruction(
                    SpirvOp.LogicalAnd,
                    _boolType,
                    didNotWrap,
                    inRange);
            }

            // EXTRACTED: ===== 56.pdf p3 ===== defines the LDS allocation range
            // check, and ===== 56.pdf p4 ===== defines invalid reads as zero and
            // invalid writes as discarded. Keep a named flag for diagnostics.
            var violation = _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                Load(_boolType, _exec),
                LogicalNot(inRange));
            Store(
                _ldsOutOfRange,
                _module.AddInstruction(
                    SpirvOp.LogicalOr,
                    _boolType,
                    Load(_boolType, _ldsOutOfRange),
                    violation));
            var safeIndex = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                inRange,
                index,
                UInt(0));
            var pointer = _module.AddInstruction(
                SpirvOp.AccessChain,
                _ldsElementPointer,
                _lds,
                safeIndex);
            return new LdsAccess(pointer, inRange);
        }

        private uint LoadLds(LdsAccess access)
        {
            var value = Load(_uintType, access.Pointer);
            return _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                access.InRange,
                value,
                UInt(0));
        }

        private void StoreLds(LdsAccess access, uint value)
        {
            var active = _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                Load(_boolType, _exec),
                access.InRange);
            var oldValue = Load(_uintType, access.Pointer);
            var selected = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                active,
                value,
                oldValue);
            Store(access.Pointer, selected);
        }

        private bool TryEmitDataShareAtomic(
            Gen5ShaderInstruction instruction,
            Gen5DataShareControl control,
            out string error)
        {
            error = string.Empty;
            var atomicOp = instruction.Opcode switch
            {
                "DsAddU32" or "DsAddRtnU32" => SpirvOp.AtomicIAdd,
                "DsSubU32" or "DsSubRtnU32" => SpirvOp.AtomicISub,
                "DsIncU32" or "DsIncRtnU32" => SpirvOp.AtomicIIncrement,
                "DsDecU32" or "DsDecRtnU32" => SpirvOp.AtomicIDecrement,
                "DsMinI32" or "DsMinRtnI32" => SpirvOp.AtomicSMin,
                "DsMaxI32" or "DsMaxRtnI32" => SpirvOp.AtomicSMax,
                "DsMinU32" or "DsMinRtnU32" => SpirvOp.AtomicUMin,
                "DsMaxU32" or "DsMaxRtnU32" => SpirvOp.AtomicUMax,
                "DsAndB32" or "DsAndRtnB32" => SpirvOp.AtomicAnd,
                "DsOrB32" or "DsOrRtnB32" => SpirvOp.AtomicOr,
                "DsXorB32" or "DsXorRtnB32" => SpirvOp.AtomicXor,
                "DsWrxchgRtnB32" => SpirvOp.AtomicExchange,
                "DsCmpstB32" or "DsCmpstRtnB32" => SpirvOp.AtomicCompareExchange,
                _ => SpirvOp.Nop,
            };
            if (atomicOp == SpirvOp.Nop)
            {
                error = $"unsupported LDS opcode {instruction.Opcode}";
                return false;
            }

            var address = GetRawSource(instruction, 0);
            var access = LdsPointer(address, control.Offset0);
            if (instruction.Destinations.Count > 0)
            {
                StoreV(instruction.Destinations[0].Value, UInt(0));
            }

            var validActiveLane = _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                Load(_boolType, _exec),
                access.InRange);
            EmitConditional(validActiveLane, () =>
            {
                var original = EmitAtomic(
                    atomicOp,
                    _uintType,
                    access.Pointer,
                    scope: 2,
                    semantics: 0x108,
                    // DS_CMPST sources: DATA0 is the comparator, DATA1 the new value.
                    value: () => GetRawSource(
                        instruction,
                        atomicOp == SpirvOp.AtomicCompareExchange ? 2 : 1),
                    comparator: () => GetRawSource(instruction, 1));
                if (instruction.Destinations.Count > 0)
                {
                    StoreV(instruction.Destinations[0].Value, original);
                }
            });

            return true;
        }

        // Maps the AMD atomic-op name suffix shared by buffer/image atomics to a SPIR-V opcode.
        // Inc/Dec approximate the AMD wrap-clamp semantics (MEM = tmp >= DATA ? 0 : tmp + 1),
        // which is exact for the common 0xFFFFFFFF clamp operand.
        private static bool TryGetAtomicOp(string name, out SpirvOp op)
        {
            op = name switch
            {
                "Swap" => SpirvOp.AtomicExchange,
                "Cmpswap" => SpirvOp.AtomicCompareExchange,
                "Add" => SpirvOp.AtomicIAdd,
                "Sub" => SpirvOp.AtomicISub,
                "Smin" => SpirvOp.AtomicSMin,
                "Umin" => SpirvOp.AtomicUMin,
                "Smax" => SpirvOp.AtomicSMax,
                "Umax" => SpirvOp.AtomicUMax,
                "And" => SpirvOp.AtomicAnd,
                "Or" => SpirvOp.AtomicOr,
                "Xor" => SpirvOp.AtomicXor,
                "Inc" => SpirvOp.AtomicIIncrement,
                "Dec" => SpirvOp.AtomicIDecrement,
                _ => SpirvOp.Nop,
            };
            return op != SpirvOp.Nop;
        }

        private uint EmitAtomic(
            SpirvOp op,
            uint type,
            uint pointer,
            uint scope,
            uint semantics,
            Func<uint> value,
            Func<uint> comparator)
        {
            if (op is SpirvOp.AtomicIIncrement or SpirvOp.AtomicIDecrement)
            {
                return _module.AddInstruction(
                    op,
                    type,
                    pointer,
                    UInt(scope),
                    UInt(semantics));
            }

            if (op == SpirvOp.AtomicCompareExchange)
            {
                // The unequal semantics must not contain Release; downgrade it to Acquire.
                return _module.AddInstruction(
                    op,
                    type,
                    pointer,
                    UInt(scope),
                    UInt(semantics),
                    UInt((semantics & ~0x8u) | 0x2u),
                    value(),
                    comparator());
            }

            return _module.AddInstruction(
                op,
                type,
                pointer,
                UInt(scope),
                UInt(semantics),
                value());
        }

        private bool TryEmitInterpolation(
            Gen5ShaderInstruction instruction,
            Gen5InterpolationControl interpolation,
            out string error)
        {
            error = string.Empty;
            if (_stage != Gen5SpirvStage.Pixel ||
                !_pixelInputs.TryGetValue(interpolation.Attribute, out var input) ||
                !TryGetVectorDestination(instruction, out var destination))
            {
                error = "invalid interpolated attribute";
                return false;
            }

            var vector = Load(_vec4Type, input);
            var component = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _floatType,
                vector,
                interpolation.Channel);
            StoreV(destination, Bitcast(_uintType, component));
            return true;
        }

        private bool TryEmitScalarMemory(
            Gen5ShaderInstruction instruction,
            Gen5ScalarMemoryControl control,
            out string error)
        {
            error = string.Empty;
            var scalarAddress = instruction.Sources.Count != 0 &&
                instruction.Sources[0].Kind == Gen5OperandKind.ScalarRegister
                ? instruction.Sources[0].Value
                : uint.MaxValue;
            if (!TryResolveDominatingBufferBinding(
                    instruction.Pc,
                    scalarAddress,
                    registerCount: instruction.Opcode.StartsWith(
                        "SBufferLoad",
                        StringComparison.Ordinal) ? 4u : 2u,
                    out var bindingIndex))
            {
                // Zero-fill is deliberately left in place: the project note on
                // this path says "trace it before changing it", because nobody
                // knows how often it fires. Making it visible is the change.
                // Its siblings TryEmitGlobalMemory and TryEmitBufferMemory fail
                // the compile in this same situation; this one keeps going, and
                // constants that read as zero produce black materials, identity
                // matrices and zero light counts in a frame that still reports
                // success.
                _zeroFilledScalarMemoryPcs.Add(instruction.Pc);
                foreach (var destination in instruction.Destinations)
                {
                    if (destination.Kind == Gen5OperandKind.ScalarRegister)
                    {
                        StoreS(destination.Value, UInt(0));
                    }
                }

                return true;
            }

            var dynamicOffset = control.DynamicOffsetRegister is { } register
                ? LoadS(register)
                : UInt(0);
            var byteAddress = IAdd(
                dynamicOffset,
                UInt(unchecked((uint)control.ImmediateOffsetBytes)));
            byteAddress = ApplyGuestBufferByteBias(bindingIndex, byteAddress);
            var dwordAddress = ShiftRightLogical(byteAddress, UInt(2));
            for (var index = 0; index < instruction.Destinations.Count; index++)
            {
                var destination = instruction.Destinations[index];
                if (destination.Kind != Gen5OperandKind.ScalarRegister)
                {
                    error = "invalid scalar-memory destination";
                    return false;
                }

                var address = index == 0
                    ? dwordAddress
                    : IAdd(dwordAddress, UInt((uint)index));
                StoreS(destination.Value, LoadBufferWord(bindingIndex, address));
            }

            return true;
        }

        /// <summary>
        /// Announces, once per shader program, that at least one SMEM load had
        /// no recoverable buffer binding and was translated as a constant zero.
        /// Deduplicated because translation runs inside the per-draw pipeline
        /// build and would otherwise print on every frame.
        /// </summary>
        private void ReportZeroFilledScalarMemory()
        {
            if (_zeroFilledScalarMemoryPcs.Count == 0)
            {
                return;
            }

            var pcs = string.Join(
                ',',
                _zeroFilledScalarMemoryPcs
                    .Take(16)
                    .Select(static pc => $"0x{pc:X4}"));
            if (_zeroFilledScalarMemoryPcs.Count > 16)
            {
                pcs += ",...";
            }

            ReportDiagnosticOnce(
                $"smem-zero-fill:{_stage}:{_state.Program.Address:X16}",
                $"[SPIRV][WARN] stage={_stage} " +
                $"program=0x{_state.Program.Address:X16}: " +
                $"{_zeroFilledScalarMemoryPcs.Count} scalar-memory load(s) had no " +
                $"recoverable buffer binding and read as zero at pc={pcs}. " +
                "Constants read as zero render black materials, identity " +
                "matrices and zero counts while the draw still succeeds.");
        }

        private bool TryEmitGlobalMemory(
            Gen5ShaderInstruction instruction,
            Gen5GlobalMemoryControl control,
            out string error)
        {
            error = string.Empty;
            if (!TryResolveDominatingBufferBinding(
                    instruction.Pc,
                    control.ScalarAddress,
                    registerCount: 2,
                    out var bindingIndex))
            {
                error = "missing global-memory binding";
                return false;
            }

            var byteAddress = IAdd(
                LoadV(control.VectorAddress),
                UInt(unchecked((uint)control.OffsetBytes)));
            byteAddress = ApplyGuestBufferByteBias(bindingIndex, byteAddress);
            var dwordAddress = ShiftRightLogical(byteAddress, UInt(2));

            if (instruction.Opcode is "GlobalAtomicAdd" or "GlobalAtomicUMax")
            {
                EmitExecConditional(() =>
                {
                    EmitConditional(IsBufferWordInRange(bindingIndex, dwordAddress), () =>
                    {
                        var original = _module.AddInstruction(
                            instruction.Opcode == "GlobalAtomicAdd"
                                ? SpirvOp.AtomicIAdd
                                : SpirvOp.AtomicUMax,
                            _uintType,
                            BufferWordPointer(bindingIndex, dwordAddress),
                            UInt(1),
                            UInt(0x48),
                            LoadV(control.VectorData));
                        if (control.Glc)
                        {
                            StoreV(control.VectorData, original);
                        }
                    });
                });
                return true;
            }

            if (instruction.Opcode.StartsWith("GlobalStore", StringComparison.Ordinal))
            {
                EmitExecConditional(() =>
                {
                    if (TryGetSubdwordStoreInfo(
                            instruction.Opcode,
                            out var byteCount,
                            out var sourceShift))
                    {
                        StoreBufferBytes(
                            bindingIndex,
                            byteAddress,
                            LoadV(control.VectorData),
                            byteCount,
                            sourceShift);
                        return;
                    }

                    for (uint index = 0; index < control.DwordCount; index++)
                    {
                        var address = index == 0
                            ? byteAddress
                            : IAdd(byteAddress, UInt(index * sizeof(uint)));
                        StoreBufferBytes(
                            bindingIndex,
                            address,
                            LoadV(control.VectorData + index),
                            sizeof(uint),
                            0);
                    }
                });
                return true;
            }

            if (TryGetSubdwordLoadInfo(
                    instruction.Opcode,
                    out var loadByteCount,
                    out var signExtend,
                    out var d16,
                    out var d16High))
            {
                StoreV(
                    control.VectorData,
                    LoadSubdwordBufferValue(
                        bindingIndex,
                        byteAddress,
                        LoadV(control.VectorData),
                        loadByteCount,
                        signExtend,
                        d16,
                        d16High));
                return true;
            }

            for (uint index = 0; index < control.DwordCount; index++)
            {
                var address = index == 0
                    ? byteAddress
                    : IAdd(byteAddress, UInt(index * sizeof(uint)));
                StoreV(
                    control.VectorData + index,
                    LoadUnalignedBufferWord(bindingIndex, address));
            }

            return true;
        }

        private bool TryEmitBufferMemory(
            Gen5ShaderInstruction instruction,
            Gen5BufferMemoryControl control,
            out string error)
        {
            error = string.Empty;
            if (_stage == Gen5SpirvStage.Vertex &&
                _vertexInputsByPc.TryGetValue(instruction.Pc, out var vertexInput))
            {
                return TryEmitVertexInputFetch(control, vertexInput, out error);
            }

            if (!TryResolveDominatingBufferBinding(
                    instruction.Pc,
                    control.ScalarResource,
                    registerCount: 4,
                    out var bindingIndex))
            {
                error = "missing buffer-memory binding";
                return false;
            }

            var scalarOffset = instruction.Sources.Count > 2
                ? GetRawSource(instruction, 2)
                : UInt(0);
            var stride = ShiftRightLogical(LoadS(control.ScalarResource + 1), UInt(16));
            stride = BitwiseAnd(stride, UInt(0x3FFF));
            var vectorIndex = control.IndexEnabled
                ? LoadV(control.VectorAddress)
                : UInt(0);
            var vectorOffset = control.OffsetEnabled
                ? LoadV(control.VectorAddress + (control.IndexEnabled ? 1u : 0u))
                : UInt(0);
            var byteAddress = IAdd(
                UInt(unchecked((uint)control.OffsetBytes)),
                scalarOffset);
            byteAddress = IAdd(byteAddress, vectorOffset);
            byteAddress = IAdd(
                byteAddress,
                _module.AddInstruction(SpirvOp.IMul, _uintType, vectorIndex, stride));
            byteAddress = ApplyGuestBufferByteBias(bindingIndex, byteAddress);
            var dwordAddress = ShiftRightLogical(byteAddress, UInt(2));

            if (instruction.Opcode.StartsWith("BufferAtomic", StringComparison.Ordinal))
            {
                if (!TryGetAtomicOp(instruction.Opcode["BufferAtomic".Length..], out var atomicOp))
                {
                    error = $"unsupported buffer opcode {instruction.Opcode}";
                    return false;
                }

                EmitExecConditional(() =>
                {
                    var inRange = IsBufferWordInRange(bindingIndex, dwordAddress);
                    EmitConditional(inRange, () =>
                    {
                        var original = EmitAtomic(
                            atomicOp,
                            _uintType,
                            BufferWordPointer(bindingIndex, dwordAddress),
                            scope: 1,
                            semantics: 0x48,
                            value: () => LoadV(control.VectorData),
                            comparator: () => LoadV(control.VectorData + 1));
                        if (control.Glc)
                        {
                            StoreV(control.VectorData, original);
                        }
                    });
                });

                return true;
            }

            if (instruction.Opcode.StartsWith("BufferStoreDword", StringComparison.Ordinal) ||
                instruction.Opcode.StartsWith("BufferStoreFormat", StringComparison.Ordinal) ||
                // MTBUF stores reach the same dispatch as MUBUF stores; only the
                // "TBuffer" prefix was missing here, so tbuffer_store_format_*
                // fell through to "unsupported buffer opcode" and dropped the
                // draw even though the matching load prefix is handled below.
                instruction.Opcode.StartsWith("TBufferStoreFormat", StringComparison.Ordinal) ||
                instruction.Opcode.StartsWith("BufferStoreByte", StringComparison.Ordinal) ||
                instruction.Opcode.StartsWith("BufferStoreShort", StringComparison.Ordinal))
            {
                EmitExecConditional(() =>
                {
                    if (TryGetSubdwordStoreInfo(
                            instruction.Opcode,
                            out var byteCount,
                            out var sourceShift))
                    {
                        StoreBufferBytes(
                            bindingIndex,
                            byteAddress,
                            LoadV(control.VectorData),
                            byteCount,
                            sourceShift);
                        return;
                    }

                    for (uint index = 0; index < control.DwordCount; index++)
                    {
                        var address = index == 0
                            ? byteAddress
                            : IAdd(byteAddress, UInt(index * sizeof(uint)));
                        StoreBufferBytes(
                            bindingIndex,
                            address,
                            LoadV(control.VectorData + index),
                            sizeof(uint),
                            0);
                    }
                });

                return true;
            }

            if (TryGetSubdwordLoadInfo(
                    instruction.Opcode,
                    out var loadByteCount,
                    out var signExtend,
                    out var d16,
                    out var d16High))
            {
                StoreV(
                    control.VectorData,
                    LoadSubdwordBufferValue(
                        bindingIndex,
                        byteAddress,
                        LoadV(control.VectorData),
                        loadByteCount,
                        signExtend,
                        d16,
                        d16High));
                return true;
            }

            if (!instruction.Opcode.StartsWith("BufferLoad", StringComparison.Ordinal) &&
                !instruction.Opcode.StartsWith("TBufferLoad", StringComparison.Ordinal))
            {
                error = $"unsupported buffer opcode {instruction.Opcode}";
                return false;
            }

            // MUBUF format loads take their element format and destination
            // swizzle from the GFX10 buffer descriptor.  Keep raw dword loads
            // on the byte-address >> 2 path below: unlike typed loads they do
            // not perform component conversion or dst_sel processing.
            // Vertex shaders normally expose indexed format loads as Vulkan
            // attributes. Loads with an additional per-lane byte offset cannot
            // be represented by a fixed attribute description, so the scalar
            // evaluator captures their descriptor as storage instead. Preserve
            // typed conversion for both MUBUF and MTBUF in that fallback path.
            if (IsFormatBufferLoad(instruction.Opcode))
            {
                EmitBufferFormatLoad(
                    bindingIndex,
                    byteAddress,
                    control.ScalarResource,
                    control.VectorData,
                    control.DwordCount,
                    control.InstructionFormat);
                return true;
            }

            for (uint index = 0; index < control.DwordCount; index++)
            {
                var address = index == 0
                    ? byteAddress
                    : IAdd(byteAddress, UInt(index * sizeof(uint)));
                StoreV(
                    control.VectorData + index,
                    LoadUnalignedBufferWord(bindingIndex, address));
            }

            return true;
        }

        private void EmitBufferFormatLoad(
            int bindingIndex,
            uint byteAddress,
            uint scalarResource,
            uint vectorData,
            uint componentCount,
            uint? instructionFormat)
        {
            var descriptorWord3 = LoadS(scalarResource + 3);
            // MTBUF carries FORMAT in the instruction. Unlike MUBUF typed
            // loads, its X/XY/XYZ/XYZW opcode selects consecutive canonical
            // components and does not apply the SRD's dst_sel swizzle. Using
            // corner table to one component and made every quad degenerate.
            var unifiedFormat = instructionFormat is { } encodedFormat
                ? UInt(encodedFormat)
                : BitwiseAnd(
                    ShiftRightLogical(descriptorWord3, UInt(12)),
                    UInt(0x7F));
            var (dataFormat, numberFormat) = DecodeGfx10BufferFormat(unifiedFormat);

            var canonical = new uint[4];
            for (var component = 0; component < canonical.Length; component++)
            {
                canonical[component] = LoadGfx10BufferFormatComponent(
                    bindingIndex,
                    byteAddress,
                    dataFormat,
                    numberFormat,
                    component);
            }

            var one = Gfx10FormatOne(numberFormat);
            for (uint destination = 0; destination < componentCount; destination++)
            {
                if (instructionFormat is not null)
                {
                    StoreV(vectorData + destination, canonical[destination]);
                    continue;
                }

                var selector = BitwiseAnd(
                    ShiftRightLogical(descriptorWord3, UInt(destination * 3)),
                    UInt(7));
                var value = UInt(0);
                value = SelectUInt(selector, 1, one, value);
                value = SelectUInt(selector, 4, canonical[0], value);
                value = SelectUInt(selector, 5, canonical[1], value);
                value = SelectUInt(selector, 6, canonical[2], value);
                value = SelectUInt(selector, 7, canonical[3], value);
                StoreV(vectorData + destination, value);
            }
        }

        private (uint DataFormat, uint NumberFormat) DecodeGfx10BufferFormat(
            uint unifiedFormat)
        {
            // The descriptor is loaded at execution time, so format decoding
            // must remain dynamic too. Generate one module-level lookup table
            // from the same authoritative decoder used by descriptor
            // evaluation rather than specializing the shader to the SRD seen
            // at compile time (compiled compute shaders may be reused with new
            // SRDs). A table also avoids emitting 77 compares at every format
            // load site, which matters in buffer-heavy compute kernels.
            if (_gfx10BufferFormatTable == 0)
            {
                const uint formatCount = 128;
                var entries = new uint[formatCount];
                for (uint format = 0; format < formatCount; format++)
                {
                    Gfx10UnifiedFormat.TryDecode(
                        format,
                        out var decodedDataFormat,
                        out var decodedNumberFormat);
                    entries[format] = UInt(
                        decodedDataFormat | (decodedNumberFormat << 8));
                }

                var tableType = _module.TypeArray(_uintType, formatCount);
                var tablePointer = _module.TypePointer(
                    SpirvStorageClass.Private,
                    tableType);
                _gfx10BufferFormatTable = _module.AddGlobalVariable(
                    tablePointer,
                    SpirvStorageClass.Private,
                    _module.ConstantComposite(tableType, entries));
                _module.AddName(_gfx10BufferFormatTable, "gfx10BufferFormats");
                _interfaces.Add(_gfx10BufferFormatTable);
            }

            var entryPointer = _module.AddInstruction(
                SpirvOp.AccessChain,
                _privateUintPointer,
                _gfx10BufferFormatTable,
                unifiedFormat);
            var entry = Load(_uintType, entryPointer);
            return (
                BitwiseAnd(entry, UInt(0xFF)),
                BitwiseAnd(
                    ShiftRightLogical(entry, UInt(8)),
                    UInt(0xFF)));
        }

        private uint LoadGfx10BufferFormatComponent(
            int bindingIndex,
            uint elementAddress,
            uint dataFormat,
            uint numberFormat,
            int component)
        {
            var byteOffset = UInt(0);
            var bitOffset = UInt(0);
            var bitCount = UInt(0);

            void SetLayout(uint format, uint bytes, uint bits, uint count)
            {
                var matches = _module.AddInstruction(
                    SpirvOp.IEqual,
                    _boolType,
                    dataFormat,
                    UInt(format));
                byteOffset = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    matches,
                    UInt(bytes),
                    byteOffset);
                bitOffset = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    matches,
                    UInt(bits),
                    bitOffset);
                bitCount = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    matches,
                    UInt(count),
                    bitCount);
            }

            // Legacy DATA_FORMAT layouts selected by the GFX10 unified format.
            // Packed formats keep their bit offset in the first dword; byte
            // offsets are used for naturally aligned vector components.
            switch (component)
            {
                case 0:
                    SetLayout(1, 0, 0, 8);   // 8
                    SetLayout(2, 0, 0, 16);  // 16
                    SetLayout(3, 0, 0, 8);   // 8_8
                    SetLayout(4, 0, 0, 32);  // 32
                    SetLayout(5, 0, 0, 16);  // 16_16
                    SetLayout(6, 0, 0, 10);  // 10_11_11
                    SetLayout(7, 0, 0, 11);  // 11_11_10
                    SetLayout(8, 0, 0, 10);  // 10_10_10_2
                    SetLayout(9, 0, 0, 2);   // 2_10_10_10
                    SetLayout(10, 0, 0, 8);  // 8_8_8_8
                    SetLayout(11, 0, 0, 32); // 32_32
                    SetLayout(12, 0, 0, 16); // 16_16_16_16
                    SetLayout(13, 0, 0, 32); // 32_32_32
                    SetLayout(14, 0, 0, 32); // 32_32_32_32
                    break;
                case 1:
                    SetLayout(3, 1, 0, 8);
                    SetLayout(5, 2, 0, 16);
                    SetLayout(6, 0, 10, 11);
                    SetLayout(7, 0, 11, 11);
                    SetLayout(8, 0, 10, 10);
                    SetLayout(9, 0, 2, 10);
                    SetLayout(10, 1, 0, 8);
                    SetLayout(11, 4, 0, 32);
                    SetLayout(12, 2, 0, 16);
                    SetLayout(13, 4, 0, 32);
                    SetLayout(14, 4, 0, 32);
                    break;
                case 2:
                    SetLayout(6, 0, 21, 11);
                    SetLayout(7, 0, 22, 10);
                    SetLayout(8, 0, 20, 10);
                    SetLayout(9, 0, 12, 10);
                    SetLayout(10, 2, 0, 8);
                    SetLayout(12, 4, 0, 16);
                    SetLayout(13, 8, 0, 32);
                    SetLayout(14, 8, 0, 32);
                    break;
                case 3:
                    SetLayout(8, 0, 30, 2);
                    SetLayout(9, 0, 22, 10);
                    SetLayout(10, 3, 0, 8);
                    SetLayout(12, 6, 0, 16);
                    SetLayout(14, 12, 0, 32);
                    break;
            }

            var packed = LoadUnalignedBufferWord(
                bindingIndex,
                IAdd(elementAddress, byteOffset));
            var raw = _module.AddInstruction(
                SpirvOp.BitFieldUExtract,
                _uintType,
                packed,
                bitOffset,
                bitCount);
            var converted = ConvertGfx10BufferComponent(
                raw,
                bitCount,
                numberFormat,
                dataFormat);
            var valid = _module.AddInstruction(
                SpirvOp.INotEqual,
                _boolType,
                bitCount,
                UInt(0));
            return _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                valid,
                converted,
                component == 3 ? Gfx10FormatOne(numberFormat) : UInt(0));
        }

        private uint ConvertGfx10BufferComponent(
            uint raw,
            uint bitCount,
            uint numberFormat,
            uint dataFormat)
        {
            var widthIs32 = _module.AddInstruction(
                SpirvOp.IEqual,
                _boolType,
                bitCount,
                UInt(32));
            var lowMask = _module.AddInstruction(
                SpirvOp.ISub,
                _uintType,
                ShiftLeftLogical(UInt(1), bitCount),
                UInt(1));
            lowMask = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                widthIs32,
                UInt(uint.MaxValue),
                lowMask);

            var signedRaw = _module.AddInstruction(
                SpirvOp.BitFieldSExtract,
                _intType,
                Bitcast(_intType, raw),
                UInt(0),
                bitCount);
            var signedBits = Bitcast(_uintType, signedRaw);
            var unsignedFloat = _module.AddInstruction(
                SpirvOp.ConvertUToF,
                _floatType,
                raw);
            var signedFloat = _module.AddInstruction(
                SpirvOp.ConvertSToF,
                _floatType,
                signedRaw);

            var unorm = Bitcast(
                _uintType,
                _module.AddInstruction(
                    SpirvOp.FDiv,
                    _floatType,
                    unsignedFloat,
                    _module.AddInstruction(
                        SpirvOp.ConvertUToF,
                        _floatType,
                        lowMask)));
            var signedMaximum = ShiftRightLogical(lowMask, UInt(1));
            var snormFloat = _module.AddInstruction(
                SpirvOp.FDiv,
                _floatType,
                signedFloat,
                _module.AddInstruction(
                    SpirvOp.ConvertUToF,
                    _floatType,
                    signedMaximum));
            snormFloat = _module.AddInstruction(
                SpirvOp.Select,
                _floatType,
                _module.AddInstruction(
                    SpirvOp.FOrdLessThan,
                    _boolType,
                    snormFloat,
                    Float(-1f)),
                Float(-1f),
                snormFloat);
            var snorm = Bitcast(_uintType, snormFloat);
            var uscaled = Bitcast(_uintType, unsignedFloat);
            var sscaled = Bitcast(_uintType, signedFloat);

            var unpackedHalf = Ext(62, _vec2Type, BitwiseAnd(raw, UInt(0xFFFF)));
            var half = Bitcast(
                _uintType,
                _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    _floatType,
                    unpackedHalf,
                    0));
            var floating = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                _module.AddInstruction(
                    SpirvOp.IEqual,
                    _boolType,
                    bitCount,
                    UInt(16)),
                half,
                raw);

            // DATA_FORMAT 10_11_11 and 11_11_10 use unsigned mini-floats
            // when NUM_FORMAT is FLOAT, not ordinary integer bit patterns.
            var isPackedFloat = _module.AddInstruction(
                SpirvOp.LogicalOr,
                _boolType,
                _module.AddInstruction(
                    SpirvOp.IEqual,
                    _boolType,
                    dataFormat,
                    UInt(6)),
                _module.AddInstruction(
                    SpirvOp.IEqual,
                    _boolType,
                    dataFormat,
                    UInt(7)));
            floating = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                isPackedFloat,
                DecodeUnsignedMiniFloat(raw, bitCount),
                floating);

            var result = raw;
            result = SelectUInt(numberFormat, 0, unorm, result);
            result = SelectUInt(numberFormat, 1, snorm, result);
            result = SelectUInt(numberFormat, 2, uscaled, result);
            result = SelectUInt(numberFormat, 3, sscaled, result);
            result = SelectUInt(numberFormat, 4, raw, result);
            result = SelectUInt(numberFormat, 5, signedBits, result);
            result = SelectUInt(numberFormat, 7, floating, result);
            return result;
        }

        private uint DecodeUnsignedMiniFloat(uint raw, uint bitCount)
        {
            var mantissaBits = _module.AddInstruction(
                SpirvOp.ISub,
                _uintType,
                bitCount,
                UInt(5));
            var mantissaMask = _module.AddInstruction(
                SpirvOp.ISub,
                _uintType,
                ShiftLeftLogical(UInt(1), mantissaBits),
                UInt(1));
            var mantissa = BitwiseAnd(raw, mantissaMask);
            var exponent = BitwiseAnd(
                ShiftRightLogical(raw, mantissaBits),
                UInt(0x1F));
            var mantissaShift = _module.AddInstruction(
                SpirvOp.ISub,
                _uintType,
                UInt(23),
                mantissaBits);
            var normalBits = BitwiseOr(
                ShiftLeftLogical(IAdd(exponent, UInt(112)), UInt(23)),
                ShiftLeftLogical(mantissa, mantissaShift));
            var subnormal = Bitcast(
                _uintType,
                _module.AddInstruction(
                    SpirvOp.FMul,
                    _floatType,
                    _module.AddInstruction(
                        SpirvOp.ConvertUToF,
                        _floatType,
                        mantissa),
                    _module.AddInstruction(
                        SpirvOp.Select,
                        _floatType,
                        _module.AddInstruction(
                            SpirvOp.IEqual,
                            _boolType,
                            mantissaBits,
                            UInt(6)),
                        Float(1f / 1_048_576f), // 2^-20 for 11-bit UFLOAT
                        Float(1f / 524_288f)))); // 2^-19 for 10-bit UFLOAT
            var special = BitwiseOr(
                UInt(0x7F800000),
                ShiftLeftLogical(mantissa, mantissaShift));
            var result = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                _module.AddInstruction(
                    SpirvOp.IEqual,
                    _boolType,
                    exponent,
                    UInt(0)),
                subnormal,
                normalBits);
            return _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                _module.AddInstruction(
                    SpirvOp.IEqual,
                    _boolType,
                    exponent,
                    UInt(31)),
                special,
                result);
        }

        private uint Gfx10FormatOne(uint numberFormat)
        {
            var isUint = _module.AddInstruction(
                SpirvOp.IEqual,
                _boolType,
                numberFormat,
                UInt(4));
            var isSint = _module.AddInstruction(
                SpirvOp.IEqual,
                _boolType,
                numberFormat,
                UInt(5));
            return _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                _module.AddInstruction(
                    SpirvOp.LogicalOr,
                    _boolType,
                    isUint,
                    isSint),
                UInt(1),
                UInt(0x3F800000));
        }

        private uint SelectUInt(
            uint selector,
            uint expected,
            uint whenTrue,
            uint whenFalse) =>
            _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                _module.AddInstruction(
                    SpirvOp.IEqual,
                    _boolType,
                    selector,
                    UInt(expected)),
                whenTrue,
                whenFalse);

        private uint LoadUnalignedBufferWord(int bindingIndex, uint byteAddress)
        {
            var result = UInt(0);
            for (uint index = 0; index < 4; index++)
            {
                var address = index == 0
                    ? byteAddress
                    : IAdd(byteAddress, UInt(index));
                var dwordAddress = ShiftRightLogical(address, UInt(2));
                var bitOffset = ShiftLeftLogical(BitwiseAnd(address, UInt(3)), UInt(3));
                var value = BitwiseAnd(
                    ShiftRightLogical(LoadBufferWord(bindingIndex, dwordAddress), bitOffset),
                    UInt(0xFF));
                result = BitwiseOr(result, ShiftLeftLogical(value, UInt(index * 8)));
            }

            return result;
        }

        private uint LoadSubdwordBufferValue(
            int bindingIndex,
            uint byteAddress,
            uint previous,
            uint byteCount,
            bool signExtend,
            bool d16,
            bool d16High)
        {
            var width = byteCount * 8;
            var raw = BitwiseAnd(
                LoadUnalignedBufferWord(bindingIndex, byteAddress),
                UInt(byteCount == 1 ? 0xFFu : 0xFFFFu));
            if (signExtend)
            {
                raw = Bitcast(
                    _uintType,
                    _module.AddInstruction(
                        SpirvOp.BitFieldSExtract,
                        _intType,
                        Bitcast(_intType, raw),
                        UInt(0),
                        UInt(width)));
            }

            if (!d16)
            {
                return raw;
            }

            var half = BitwiseAnd(raw, UInt(0xFFFF));
            return d16High
                ? BitwiseOr(
                    BitwiseAnd(previous, UInt(0x0000_FFFF)),
                    ShiftLeftLogical(half, UInt(16)))
                : BitwiseOr(
                    BitwiseAnd(previous, UInt(0xFFFF_0000)),
                    half);
        }

        private void StoreBufferBytes(
            int bindingIndex,
            uint byteAddress,
            uint value,
            uint byteCount,
            uint sourceShift)
        {
            value = ShiftRightLogical(value, UInt(sourceShift));
            for (uint index = 0; index < byteCount; index++)
            {
                var address = index == 0
                    ? byteAddress
                    : IAdd(byteAddress, UInt(index));
                var dwordAddress = ShiftRightLogical(address, UInt(2));
                var shift = ShiftLeftLogical(BitwiseAnd(address, UInt(3)), UInt(3));
                var oldValue = LoadBufferWord(bindingIndex, dwordAddress);
                var byteMask = ShiftLeftLogical(UInt(0xFF), shift);
                var sourceByte = BitwiseAnd(
                    ShiftRightLogical(value, UInt(index * 8)),
                    UInt(0xFF));
                var updated = BitwiseOr(
                    BitwiseAnd(
                        oldValue,
                        _module.AddInstruction(SpirvOp.Not, _uintType, byteMask)),
                    ShiftLeftLogical(sourceByte, shift));
                StoreBufferWord(bindingIndex, dwordAddress, updated);
            }
        }

        private static bool TryGetSubdwordLoadInfo(
            string opcode,
            out uint byteCount,
            out bool signExtend,
            out bool d16,
            out bool d16High)
        {
            byteCount = opcode.Contains("byte", StringComparison.OrdinalIgnoreCase) ? 1u : 2u;
            signExtend = opcode.Contains("Sbyte", StringComparison.Ordinal) ||
                opcode.Contains("Sshort", StringComparison.Ordinal);
            d16 = opcode.Contains("D16", StringComparison.Ordinal);
            d16High = opcode.EndsWith("D16Hi", StringComparison.Ordinal);
            return opcode.Contains("LoadUbyte", StringComparison.Ordinal) ||
                opcode.Contains("LoadSbyte", StringComparison.Ordinal) ||
                opcode.Contains("LoadUshort", StringComparison.Ordinal) ||
                opcode.Contains("LoadSshort", StringComparison.Ordinal) ||
                opcode.Contains("LoadShortD16", StringComparison.Ordinal);
        }

        private static bool TryGetSubdwordStoreInfo(
            string opcode,
            out uint byteCount,
            out uint sourceShift)
        {
            byteCount = opcode.Contains("StoreByte", StringComparison.Ordinal) ? 1u : 2u;
            sourceShift = opcode.EndsWith("D16Hi", StringComparison.Ordinal) ? 16u : 0u;
            return opcode.Contains("StoreByte", StringComparison.Ordinal) ||
                opcode.Contains("StoreShort", StringComparison.Ordinal);
        }

        private static bool IsFormatBufferLoad(string opcode) =>
            opcode.StartsWith("BufferLoadFormat", StringComparison.Ordinal) ||
            opcode.StartsWith("TBufferLoadFormat", StringComparison.Ordinal);

        private static bool UsesSampler(string opcode) =>
            opcode.StartsWith("ImageSample", StringComparison.Ordinal) ||
            opcode.StartsWith("ImageGather", StringComparison.Ordinal);

        private bool TryResolveDominatingBufferBinding(
            uint pc,
            uint scalarRegister,
            uint registerCount,
            out int bindingIndex)
        {
            if (_bufferBindingByPc.TryGetValue(pc, out bindingIndex))
            {
                return true;
            }

            for (var index = 0; index < _evaluation.GlobalMemoryBindings.Count; index++)
            {
                var binding = _evaluation.GlobalMemoryBindings[index];
                if (binding.ScalarAddress != scalarRegister)
                {
                    continue;
                }

                foreach (var candidatePc in binding.InstructionPcs)
                {
                    if (!HasSameScalarDefinitions(
                            candidatePc,
                            pc,
                            scalarRegister,
                            registerCount))
                    {
                        continue;
                    }

                    bindingIndex = _globalBufferBase + index;
                    _bufferBindingByPc.Add(pc, bindingIndex);
                    return true;
                }
            }

            bindingIndex = -1;
            return false;
        }

        private bool TryResolveDominatingImageBinding(
            Gen5ShaderInstruction instruction,
            Gen5ImageControl control,
            out int bindingIndex)
        {
            if (_imageBindingByPc.TryGetValue(instruction.Pc, out bindingIndex) &&
                bindingIndex < _imageResources.Count)
            {
                return true;
            }

            var imageLoad = Gen5ShaderTranslator.IsImageLoadOperation(instruction.Opcode);
            var storage = Gen5ShaderTranslator.IsStorageImageOperation(instruction.Opcode);
            for (var index = 0; index < _evaluation.ImageBindings.Count; index++)
            {
                var candidate = _evaluation.ImageBindings[index];
                if (candidate.Control.ScalarResource != control.ScalarResource ||
                    candidate.Control.ScalarSampler != control.ScalarSampler ||
                    Gen5ShaderTranslator.IsImageLoadOperation(candidate.Opcode) != imageLoad ||
                    Gen5ShaderTranslator.IsStorageImageOperation(candidate.Opcode) != storage ||
                    !HasSameScalarDefinitions(
                        candidate.Pc,
                        instruction.Pc,
                        control.ScalarResource,
                        ImageDescriptorDwords) ||
                    UsesSampler(instruction.Opcode) &&
                    !HasSameScalarDefinitions(
                        candidate.Pc,
                        instruction.Pc,
                        control.ScalarSampler,
                        SamplerDescriptorDwords))
                {
                    continue;
                }

                bindingIndex = index;
                _imageBindingByPc.Add(instruction.Pc, index);
                return true;
            }

            bindingIndex = -1;
            return false;
        }

        private bool HasSameScalarDefinitions(
            uint candidatePc,
            uint targetPc,
            uint firstRegister,
            uint registerCount)
        {
            if (firstRegister + registerCount > ScalarRegisterCount ||
                !_scalarDefinitionsBeforePc.TryGetValue(candidatePc, out var candidate) ||
                !_scalarDefinitionsBeforePc.TryGetValue(targetPc, out var target))
            {
                return false;
            }

            for (var register = firstRegister;
                 register < firstRegister + registerCount;
                 register++)
            {
                var definition = candidate[register];
                if (definition is ConflictingScalarDefinition or
                        UnreachableScalarDefinition ||
                    target[register] != definition)
                {
                    return false;
                }
            }

            return true;
        }

        private bool TryEmitVertexInputFetch(
            Gen5BufferMemoryControl control,
            SpirvVertexInput input,
            out string error)
        {
            error = string.Empty;
            if (control.DwordCount == 0 ||
                control.DwordCount > input.ComponentCount)
            {
                error =
                    $"invalid vertex input fetch components={control.DwordCount} " +
                    $"input={input.ComponentCount}";
                return false;
            }

            var loaded = Load(input.Type, input.Variable);
            for (uint component = 0; component < control.DwordCount; component++)
            {
                var value = input.ComponentCount == 1
                    ? loaded
                    : _module.AddInstruction(
                        SpirvOp.CompositeExtract,
                        input.ComponentType,
                        loaded,
                        component);
                var raw = input.ComponentKind == VertexInputComponentKind.Uint
                    ? value
                    : Bitcast(_uintType, value);
                StoreV(control.VectorData + component, raw);
            }

            return true;
        }

        private bool TryEmitImage(
            Gen5ShaderInstruction instruction,
            Gen5ImageControl image,
            out string error)
        {
            error = string.Empty;
            if (!TryResolveDominatingImageBinding(instruction, image, out var bindingIndex))
            {
                var candidates = _evaluation.ImageBindings
                    .Where(binding =>
                        binding.Control.ScalarResource == image.ScalarResource &&
                        binding.Control.ScalarSampler == image.ScalarSampler)
                    .Take(16)
                    .Select(binding =>
                        $"{binding.Opcode}@0x{binding.Pc:X}" +
                        $"/r={HasSameScalarDefinitions(binding.Pc, instruction.Pc, image.ScalarResource, ImageDescriptorDwords)}" +
                        $"/s={!UsesSampler(instruction.Opcode) || HasSameScalarDefinitions(binding.Pc, instruction.Pc, image.ScalarSampler, SamplerDescriptorDwords)}");
                error =
                    $"unresolved image binding t=s{image.ScalarResource} " +
                    $"s=s{image.ScalarSampler} " +
                    $"candidates=[{string.Join(',', candidates)}]";
                return false;
            }

            var resource = _imageResources[bindingIndex];
            var imageObject = Load(resource.ObjectType, resource.Variable);
            if (instruction.Opcode == "ImageGetResinfo")
            {
                var queryImage = resource.IsStorage
                    ? imageObject
                    : _module.AddInstruction(
                        SpirvOp.Image,
                        resource.ImageType,
                        imageObject);
                var size = _module.AddInstruction(
                    resource.IsStorage
                        ? SpirvOp.ImageQuerySize
                        : SpirvOp.ImageQuerySizeLod,
                    _module.TypeVector(_intType, 2),
                    resource.IsStorage
                        ? [queryImage]
                        : [queryImage, UInt(0)]);
                uint outputIndex = 0;
                for (uint component = 0; component < 4; component++)
                {
                    if ((image.Dmask & (1u << (int)component)) == 0)
                    {
                        continue;
                    }

                    uint value;
                    if (component < 2)
                    {
                        var signedValue = _module.AddInstruction(
                            SpirvOp.CompositeExtract,
                            _intType,
                            size,
                            component);
                        value = Bitcast(_uintType, signedValue);
                    }
                    else
                    {
                        value = UInt(1);
                    }

                    StoreV(image.VectorData + outputIndex++, value);
                }

                return true;
            }

            if (instruction.Opcode is "ImageStore" or "ImageStoreMip")
            {
                if (!resource.IsStorage)
                {
                    error = "image store is not bound as storage";
                    return false;
                }

                var coordinates = BuildIntegerCoordinates(image, 0);
                var components = new uint[4];
                uint sourceIndex = 0;
                for (var component = 0; component < components.Length; component++)
                {
                    if ((image.Dmask & (1u << component)) != 0)
                    {
                        var raw = LoadImageStoreComponent(
                            image,
                            resource,
                            sourceIndex++);
                        components[component] = resource.ComponentKind switch
                        {
                            ImageComponentKind.Sint => Bitcast(_intType, raw),
                            ImageComponentKind.Uint => raw,
                            _ => Bitcast(_floatType, raw),
                        };
                    }
                    else
                    {
                        components[component] = resource.ComponentKind switch
                        {
                            ImageComponentKind.Sint =>
                                _module.Constant(_intType, 0),
                            ImageComponentKind.Uint => UInt(0),
                            _ => Float(0),
                        };
                    }
                }

                var texel = _module.AddInstruction(
                    SpirvOp.CompositeConstruct,
                    resource.VectorType,
                    components);
                var imageSize = _module.AddInstruction(
                    SpirvOp.ImageQuerySize,
                    _module.TypeVector(_intType, 2),
                    imageObject);
                EmitBoundsCheckedImageWrite(
                    coordinates,
                    imageSize,
                    imageObject,
                    texel);

                return true;
            }

            if (instruction.Opcode.StartsWith("ImageAtomic", StringComparison.Ordinal))
            {
                if (!resource.IsStorage)
                {
                    error = "image atomic is not bound as storage";
                    return false;
                }

                if (resource.ComponentKind == ImageComponentKind.Float ||
                    !TryGetAtomicOp(instruction.Opcode["ImageAtomic".Length..], out var atomicOp))
                {
                    error = $"unsupported storage image opcode {instruction.Opcode}";
                    return false;
                }

                var signed = resource.ComponentKind == ImageComponentKind.Sint;
                var atomicImageSize = _module.AddInstruction(
                    SpirvOp.ImageQuerySize,
                    _module.TypeVector(_intType, 2),
                    imageObject);
                var coordinates = BuildClampedIntegerCoordinates(
                    image,
                    0,
                    atomicImageSize);
                EmitExecConditional(() =>
                {
                    var pointer = _module.AddInstruction(
                        SpirvOp.ImageTexelPointer,
                        _module.TypePointer(SpirvStorageClass.Image, resource.ComponentType),
                        resource.Variable,
                        coordinates,
                        UInt(0));
                    uint LoadData(uint register) => signed
                        ? Bitcast(_intType, LoadV(register))
                        : LoadV(register);
                    var original = EmitAtomic(
                        atomicOp,
                        resource.ComponentType,
                        pointer,
                        scope: 1,
                        semantics: 0x808,
                        value: () => LoadData(image.VectorData),
                        comparator: () => LoadData(image.VectorData + 1));
                    if (image.Glc)
                    {
                        StoreV(
                            image.VectorData,
                            signed ? Bitcast(_uintType, original) : original);
                    }
                });

                return true;
            }

            if (resource.IsStorage &&
                instruction.Opcode is not ("ImageLoad" or "ImageLoadMip"))
            {
                error = $"unsupported storage image opcode {instruction.Opcode}";
                return false;
            }

            uint sampled;
            var writeAllComponents = false;
            if (instruction.Opcode is "ImageLoad" or "ImageLoadMip")
            {
                if (resource.IsStorage)
                {
                    var imageSize = _module.AddInstruction(
                        SpirvOp.ImageQuerySize,
                        _module.TypeVector(_intType, 2),
                        imageObject);
                    var coordinates = BuildClampedIntegerCoordinates(
                        image,
                        0,
                        imageSize);
                    sampled = _module.AddInstruction(
                        SpirvOp.ImageRead,
                        resource.VectorType,
                        imageObject,
                        coordinates);
                }
                else
                {
                    var mipLevel = _evaluation.ImageBindings[bindingIndex].MipLevel ?? 0;
                    var fetchedImage = _module.AddInstruction(
                        SpirvOp.Image,
                        resource.ImageType,
                        imageObject);
                    var imageSize = _module.AddInstruction(
                        SpirvOp.ImageQuerySizeLod,
                        _module.TypeVector(_intType, 2),
                        fetchedImage,
                        UInt(mipLevel));
                    var coordinates = BuildClampedIntegerCoordinates(
                        image,
                        0,
                        imageSize);
                    sampled = _module.AddInstruction(
                        SpirvOp.ImageFetch,
                        resource.VectorType,
                        fetchedImage,
                        coordinates,
                        2,
                        UInt(mipLevel));
                }
            }
            else if (instruction.Opcode.StartsWith(
                         "ImageSample",
                         StringComparison.Ordinal))
            {
                var hasOffset =
                    instruction.Opcode.EndsWith("O", StringComparison.Ordinal);
                var hasCompare =
                    instruction.Opcode.Contains("SampleC", StringComparison.Ordinal);
                var hasGradients =
                    instruction.Opcode.Contains("SampleD", StringComparison.Ordinal);
                var hasZeroLod =
                    instruction.Opcode.Contains("Lz", StringComparison.Ordinal);
                var hasLod = !hasZeroLod &&
                    instruction.Opcode.Contains("SampleL", StringComparison.Ordinal);
                var hasBias =
                    instruction.Opcode.Contains("SampleB", StringComparison.Ordinal);

                // RDNA MIMG address operands are ordered as
                // {offset}{bias/lod}{z-compare}{derivatives}{body}.  The old
                // lowering treated SAMPLE_D as body-first and consequently
                // sampled gradients as coordinates in every captured
                // derivative operation.
                var addressCursor = 0;
                var offset = 0u;
                if (hasOffset)
                {
                    addressCursor = AlignFullImageAddress(image, addressCursor);
                    offset = BuildImageOffset(image, addressCursor);
                    addressCursor += ImageFullAddressSlots(image);
                }

                // SAMPLE_B prefixes the body with a bias. SAMPLE_L instead
                // carries LOD as the final body component (x, y, lod for 2D),
                // per the RDNA image-address table.
                var lodOrBias = hasBias
                    ? LoadImageFloatAddress(image, addressCursor++)
                    : 0u;
                var reference = 0u;
                if (hasCompare)
                {
                    // PCF references remain full-width even when A16 packs the
                    // ordinary address components two per VGPR.
                    addressCursor = AlignFullImageAddress(image, addressCursor);
                    reference = Bitcast(
                        _floatType,
                        LoadV(image.GetAddressRegister(
                            ImageAddressRegister(image, addressCursor))));
                    addressCursor += ImageFullAddressSlots(image);
                }

                var gradientX = hasGradients
                    ? BuildFloatCoordinates(image, addressCursor)
                    : 0u;
                var gradientY = hasGradients
                    ? BuildFloatCoordinates(image, addressCursor + 2)
                    : 0u;
                if (hasGradients)
                {
                    addressCursor += 4;
                }

                var coordinates = resource.Arrayed
                    ? BuildFloatArrayCoordinates(image, addressCursor)
                    : BuildFloatCoordinates(image, addressCursor);
                var explicitLod = hasGradients || hasZeroLod || hasLod;
                var lod = hasZeroLod
                    ? Float(0)
                    : hasLod
                        ? LoadImageFloatAddress(
                            image,
                            addressCursor + (resource.Arrayed ? 3 : 2))
                        : lodOrBias;
                if (hasOffset)
                {
                    // Vulkan before maintenance8 forbids the dynamic Offset
                    // image operand on non-gather sampling operations. RDNA
                    // offsets are per-lane VGPR values, so ConstOffset is not
                    // equivalent. Fold the texel offset into normalized sample
                    // coordinates using the queried mip extent instead.
                    var offsetLod = explicitLod && !hasGradients
                        ? lod
                        : Float(0);
                    coordinates = ApplyDynamicSampleOffset(
                        resource,
                        imageObject,
                        coordinates,
                        offset,
                        offsetLod);
                }

                var imageOperands =
                    hasGradients ? 4u : explicitLod ? 2u : hasBias ? 1u : 0u;
                var operands = new List<uint>
                {
                    imageObject,
                    coordinates,
                };

                if (imageOperands != 0)
                {
                    operands.Add(imageOperands);
                    if (hasGradients)
                    {
                        operands.Add(gradientX);
                        operands.Add(gradientY);
                    }
                    else if (explicitLod)
                    {
                        operands.Add(lod);
                    }
                    else if (hasBias)
                    {
                        operands.Add(lodOrBias);
                    }

                }

                sampled = _module.AddInstruction(
                    explicitLod
                        ? SpirvOp.ImageSampleExplicitLod
                        : SpirvOp.ImageSampleImplicitLod,
                    resource.VectorType,
                    [.. operands]);
                if (hasCompare)
                {
                    sampled = EmitManualDepthCompare(resource, sampled, reference);
                }
            }
            else if (instruction.Opcode.StartsWith(
                         "ImageGather4",
                         StringComparison.Ordinal))
            {
                var hasOffset =
                    instruction.Opcode.EndsWith("O", StringComparison.Ordinal);
                var hasCompare =
                    instruction.Opcode.Contains("Gather4C", StringComparison.Ordinal);
                var addressCursor = 0;
                var offset = 0u;
                if (hasOffset)
                {
                    offset = BuildImageOffset(image, addressCursor);
                    addressCursor += ImageFullAddressSlots(image);
                }

                var reference = 0u;
                if (hasCompare)
                {
                    addressCursor = AlignFullImageAddress(image, addressCursor);
                    reference = Bitcast(
                        _floatType,
                        LoadV(image.GetAddressRegister(
                            ImageAddressRegister(image, addressCursor))));
                    addressCursor += ImageFullAddressSlots(image);
                }

                var coordinates = resource.Arrayed
                    ? BuildFloatArrayCoordinates(image, addressCursor)
                    : BuildFloatCoordinates(image, addressCursor);
                var operands = new List<uint>
                {
                    imageObject,
                    coordinates,
                };
                if (hasCompare)
                {
                    operands.Add(UInt(0));
                }
                else
                {
                    uint component = 0;
                    while (component < 3 &&
                           (image.Dmask & (1u << (int)component)) == 0)
                    {
                        component++;
                    }

                    operands.Add(UInt(component));
                }

                if (hasOffset)
                {
                    operands.Add(0x10u);
                    operands.Add(offset);
                }

                sampled = _module.AddInstruction(
                    SpirvOp.ImageGather,
                    resource.VectorType,
                    [.. operands]);
                if (hasCompare)
                {
                    var compared = new uint[4];
                    for (var component = 0u; component < 4; component++)
                    {
                        var texel = _module.AddInstruction(
                            SpirvOp.CompositeExtract,
                            resource.ComponentType,
                            sampled,
                            component);
                        compared[component] = EmitDepthCompareScalar(resource, texel, reference);
                    }

                    sampled = _module.AddInstruction(
                        SpirvOp.CompositeConstruct,
                        resource.VectorType,
                        compared);
                }

                writeAllComponents = true;
            }
            else
            {
                error = $"unsupported image opcode {instruction.Opcode}";
                return false;
            }

            var outputValues = new List<uint>(4);
            for (uint component = 0; component < 4; component++)
            {
                if (!writeAllComponents &&
                    (image.Dmask & (1u << (int)component)) == 0)
                {
                    continue;
                }

                var value = _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    resource.ComponentType,
                    sampled,
                    component);
                var raw = resource.ComponentKind switch
                {
                    ImageComponentKind.Uint => value,
                    _ => Bitcast(_uintType, value),
                };
                outputValues.Add(raw);
            }

            if (_stage == Gen5SpirvStage.Pixel &&
                PixelImageCaptureAddressMatches() &&
                uint.TryParse(
                    Environment.GetEnvironmentVariable(
                        "PROSPERISMO_CAPTURE_PIXEL_IMAGE_PC"),
                    out var captureImagePc) &&
                instruction.Pc == captureImagePc)
            {
                var captureBase = 248u;
                if (uint.TryParse(
                        Environment.GetEnvironmentVariable(
                            "PROSPERISMO_CAPTURE_PIXEL_IMAGE_VGPR_BASE"),
                        out var requestedCaptureBase))
                {
                    captureBase = requestedCaptureBase;
                }
                captureBase = captureBase <= 252 ? captureBase : 248u;
                for (var component = 0; component < 4; component++)
                {
                    StoreV(
                        captureBase + (uint)component,
                        component < outputValues.Count
                            ? outputValues[component]
                            : Bitcast(_uintType, Float(1)));
                }
            }

            if (image.D16)
            {
                for (var index = 0; index < outputValues.Count; index += 2)
                {
                    var low = outputValues[index];
                    var high = index + 1 < outputValues.Count
                        ? outputValues[index + 1]
                        : UInt(0);
                    StoreV(
                        image.VectorData + (uint)(index / 2),
                        PackImageD16(resource, low, high));
                }
            }
            else
            {
                for (var index = 0; index < outputValues.Count; index++)
                {
                    StoreV(image.VectorData + (uint)index, outputValues[index]);
                }
            }

            return true;
        }

        private uint EmitDepthCompareScalar(
            SpirvImageResource resource,
            uint texel,
            uint reference)
        {
            var texelAsFloat = resource.ComponentKind switch
            {
                ImageComponentKind.Uint => _module.AddInstruction(
                    SpirvOp.ConvertUToF, _floatType, texel),
                ImageComponentKind.Sint => _module.AddInstruction(
                    SpirvOp.ConvertSToF, _floatType, texel),
                _ => texel,
            };
            var passes = _module.AddInstruction(
                SpirvOp.FOrdLessThanEqual,
                _boolType,
                reference,
                texelAsFloat);
            return _module.AddInstruction(
                SpirvOp.Select,
                resource.ComponentType,
                passes,
                resource.ComponentKind switch
                {
                    ImageComponentKind.Uint => UInt(1),
                    ImageComponentKind.Sint => _module.Constant(_intType, 1),
                    _ => Float(1),
                },
                resource.ComponentKind switch
                {
                    ImageComponentKind.Uint => UInt(0),
                    ImageComponentKind.Sint => _module.Constant(_intType, 0),
                    _ => Float(0),
                });
        }

        private uint EmitManualDepthCompare(
            SpirvImageResource resource,
            uint sampledVector,
            uint reference)
        {
            var texel = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                resource.ComponentType,
                sampledVector,
                0u);
            var scalar = EmitDepthCompareScalar(resource, texel, reference);
            return _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                resource.VectorType,
                scalar,
                scalar,
                scalar,
                resource.ComponentKind switch
                {
                    ImageComponentKind.Uint => UInt(1),
                    ImageComponentKind.Sint => _module.Constant(_intType, 1),
                    _ => Float(1),
                });
        }

        private uint BuildFloatCoordinates(Gen5ImageControl image, int start)
        {
            var x = LoadImageFloatAddress(image, start);
            var y = LoadImageFloatAddress(image, start + 1);
            return _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                _vec2Type,
                x,
                y);
        }

        private uint BuildFloatArrayCoordinates(Gen5ImageControl image, int start)
        {
            var x = LoadImageFloatAddress(image, start);
            var y = LoadImageFloatAddress(image, start + 1);
            var slice = LoadImageFloatAddress(image, start + 2);
            return _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                _vec3Type,
                x,
                y,
                slice);
        }

        private static int ImageAddressRegister(
            Gen5ImageControl image,
            int component) => image.A16 ? component / 2 : component;

        private static int ImageFullAddressSlots(Gen5ImageControl image) =>
            image.A16 ? 2 : 1;

        private static int AlignFullImageAddress(
            Gen5ImageControl image,
            int component) => image.A16 ? (component + 1) & ~1 : component;

        private uint LoadImageFloatAddress(Gen5ImageControl image, int component)
        {
            var raw = LoadV(image.GetAddressRegister(
                ImageAddressRegister(image, component)));
            if (!image.A16)
            {
                return Bitcast(_floatType, raw);
            }

            var unpacked = Ext(62, _vec2Type, raw);
            return _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _floatType,
                unpacked,
                (uint)(component & 1));
        }

        private uint LoadImageIntegerAddress(Gen5ImageControl image, int component)
        {
            var raw = LoadV(image.GetAddressRegister(
                ImageAddressRegister(image, component)));
            if (!image.A16)
            {
                return raw;
            }

            return BitwiseAnd(
                ShiftRightLogical(raw, UInt((uint)((component & 1) * 16))),
                UInt(0xFFFF));
        }

        private uint LoadImageStoreComponent(
            Gen5ImageControl image,
            SpirvImageResource resource,
            uint component)
        {
            if (!image.D16)
            {
                return LoadV(image.VectorData + component);
            }

            var packed = LoadV(image.VectorData + component / 2);
            if (resource.ComponentKind == ImageComponentKind.Float)
            {
                var unpacked = Ext(62, _vec2Type, packed);
                return Bitcast(
                    _uintType,
                    _module.AddInstruction(
                        SpirvOp.CompositeExtract,
                        _floatType,
                        unpacked,
                        component & 1));
            }

            var shifted = ShiftRightLogical(packed, UInt((component & 1) * 16));
            var low = BitwiseAnd(shifted, UInt(0xFFFF));
            if (resource.ComponentKind != ImageComponentKind.Sint)
            {
                return low;
            }

            return Bitcast(
                _uintType,
                _module.AddInstruction(
                    SpirvOp.BitFieldSExtract,
                    _intType,
                    Bitcast(_intType, low),
                    UInt(0),
                    UInt(16)));
        }

        private uint PackImageD16(
            SpirvImageResource resource,
            uint low,
            uint high)
        {
            if (resource.ComponentKind == ImageComponentKind.Float)
            {
                var pair = _module.AddInstruction(
                    SpirvOp.CompositeConstruct,
                    _vec2Type,
                    Bitcast(_floatType, low),
                    Bitcast(_floatType, high));
                return Ext(58, _uintType, pair);
            }

            return BitwiseOr(
                BitwiseAnd(low, UInt(0xFFFF)),
                ShiftLeftLogical(BitwiseAnd(high, UInt(0xFFFF)), UInt(16)));
        }

        private uint BuildIntegerCoordinates(Gen5ImageControl image, int start)
        {
            var ivec2 = _module.TypeVector(_intType, 2);
            var x = Bitcast(_intType, LoadImageIntegerAddress(image, start));
            var y = Bitcast(_intType, LoadImageIntegerAddress(image, start + 1));
            return _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                ivec2,
                x,
                y);
        }

        private uint BuildClampedIntegerCoordinates(
            Gen5ImageControl image,
            int start,
            uint imageSize)
        {
            var ivec2 = _module.TypeVector(_intType, 2);
            var x = ClampSignedCoordinate(
                Bitcast(
                    _intType,
                    LoadImageIntegerAddress(image, start)),
                _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    _intType,
                    imageSize,
                    0));
            var y = ClampSignedCoordinate(
                Bitcast(
                    _intType,
                    LoadImageIntegerAddress(image, start + 1)),
                _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    _intType,
                    imageSize,
                    1));
            return _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                ivec2,
                x,
                y);
        }

        private uint ClampSignedCoordinate(uint value, uint extent)
        {
            var zero = _module.Constant(_intType, 0);
            var max = _module.AddInstruction(
                SpirvOp.ISub,
                _intType,
                extent,
                _module.Constant(_intType, 1));
            var belowZero = _module.AddInstruction(
                SpirvOp.SLessThan,
                _boolType,
                value,
                zero);
            var atLeastZero = _module.AddInstruction(
                SpirvOp.Select,
                _intType,
                belowZero,
                zero,
                value);
            var aboveMax = _module.AddInstruction(
                SpirvOp.SGreaterThan,
                _boolType,
                atLeastZero,
                max);
            return _module.AddInstruction(
                SpirvOp.Select,
                _intType,
                aboveMax,
                max,
                atLeastZero);
        }

        private void EmitBoundsCheckedImageWrite(
            uint coordinates,
            uint imageSize,
            uint imageObject,
            uint texel)
        {
            var x = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _intType,
                coordinates,
                0);
            var y = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _intType,
                coordinates,
                1);
            var width = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _intType,
                imageSize,
                0);
            var height = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _intType,
                imageSize,
                1);
            var zero = _module.Constant(_intType, 0);
            var xNonNegative = _module.AddInstruction(
                SpirvOp.SGreaterThanEqual,
                _boolType,
                x,
                zero);
            var yNonNegative = _module.AddInstruction(
                SpirvOp.SGreaterThanEqual,
                _boolType,
                y,
                zero);
            var xInRange = _module.AddInstruction(
                SpirvOp.SLessThan,
                _boolType,
                x,
                width);
            var yInRange = _module.AddInstruction(
                SpirvOp.SLessThan,
                _boolType,
                y,
                height);
            var lowerInRange = _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                xNonNegative,
                yNonNegative);
            var upperInRange = _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                xInRange,
                yInRange);
            var inRange = _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                lowerInRange,
                upperInRange);
            inRange = _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                Load(_boolType, _exec),
                inRange);
            var writeLabel = _module.AllocateId();
            var mergeLabel = _module.AllocateId();
            _module.AddStatement(SpirvOp.SelectionMerge, mergeLabel, 0);
            _module.AddStatement(
                SpirvOp.BranchConditional,
                inRange,
                writeLabel,
                mergeLabel);
            _module.AddLabel(writeLabel);
            _module.AddStatement(
                SpirvOp.ImageWrite,
                imageObject,
                coordinates,
                texel);
            _module.AddStatement(SpirvOp.Branch, mergeLabel);
            _module.AddLabel(mergeLabel);
        }

        private uint BuildImageOffset(Gen5ImageControl image, int component)
        {
            var ivec2 = _module.TypeVector(_intType, 2);
            var packed = Bitcast(
                _intType,
                LoadV(image.GetAddressRegister(
                    ImageAddressRegister(image, component))));
            var x = _module.AddInstruction(
                SpirvOp.BitFieldSExtract,
                _intType,
                packed,
                UInt(0),
                UInt(6));
            var y = _module.AddInstruction(
                SpirvOp.BitFieldSExtract,
                _intType,
                packed,
                UInt(8),
                UInt(6));
            return _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                ivec2,
                x,
                y);
        }

        private uint ApplyDynamicSampleOffset(
            SpirvImageResource resource,
            uint sampledImage,
            uint coordinates,
            uint texelOffset,
            uint lod)
        {
            var ivec2 = _module.TypeVector(_intType, 2);
            var image = _module.AddInstruction(
                SpirvOp.Image,
                resource.ImageType,
                sampledImage);
            var signedLod = _module.AddInstruction(
                SpirvOp.ConvertFToS,
                _intType,
                lod);
            var lodIsNegative = _module.AddInstruction(
                SpirvOp.SLessThan,
                _boolType,
                signedLod,
                _module.Constant(_intType, 0));
            var clampedLod = _module.AddInstruction(
                SpirvOp.Select,
                _intType,
                lodIsNegative,
                _module.Constant(_intType, 0),
                signedLod);
            var size = _module.AddInstruction(
                SpirvOp.ImageQuerySizeLod,
                resource.Arrayed ? _module.TypeVector(_intType, 3) : ivec2,
                image,
                clampedLod);
            if (resource.Arrayed)
            {
                size = _module.AddInstruction(
                    SpirvOp.VectorShuffle,
                    ivec2,
                    size,
                    size,
                    0u,
                    1u);
            }

            var sizeFloat = _module.AddInstruction(
                SpirvOp.ConvertSToF,
                _vec2Type,
                size);
            var offsetFloat = _module.AddInstruction(
                SpirvOp.ConvertSToF,
                _vec2Type,
                texelOffset);
            var normalizedOffset = _module.AddInstruction(
                SpirvOp.FDiv,
                _vec2Type,
                offsetFloat,
                sizeFloat);
            if (!resource.Arrayed)
            {
                return _module.AddInstruction(
                    SpirvOp.FAdd,
                    _vec2Type,
                    coordinates,
                    normalizedOffset);
            }

            var offsetVec3 = _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                _vec3Type,
                _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    _floatType,
                    normalizedOffset,
                    0u),
                _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    _floatType,
                    normalizedOffset,
                    1u),
                Float(0));
            return _module.AddInstruction(
                SpirvOp.FAdd,
                _vec3Type,
                coordinates,
                offsetVec3);
        }

        private bool TryEmitExport(
            Gen5ShaderInstruction instruction,
            Gen5ExportControl export,
            out string error)
        {
            error = string.Empty;
            if (instruction.Sources.Count < 4)
            {
                error = "missing export sources";
                return false;
            }

            if (_stage == Gen5SpirvStage.Pixel)
            {
                if (!_pixelOutputs.TryGetValue(export.Target, out var output))
                {
                    return true;
                }

                Store(_reachedPixelExport, _module.ConstantBool(true));

                var values = new uint[4];
                for (var component = 0; component < 4; component++)
                {
                    var enabled = (export.EnableMask & (1u << component)) != 0;
                    if (!enabled)
                    {
                        values[component] = _module.AddInstruction(
                            SpirvOp.CompositeExtract,
                            output.Kind switch
                            {
                                Gen5PixelOutputKind.Uint => _uintType,
                                Gen5PixelOutputKind.Sint => _intType,
                                _ => _floatType,
                            },
                            Load(output.Type, output.Variable),
                            (uint)component);
                        continue;
                    }

                    if (export.Compressed)
                    {
                        var value = LoadCompressedExportComponent(
                            instruction,
                            component);
                        values[component] = output.Kind switch
                        {
                            Gen5PixelOutputKind.Uint => _module.AddInstruction(
                                SpirvOp.ConvertFToU,
                                _uintType,
                                value),
                            Gen5PixelOutputKind.Sint => _module.AddInstruction(
                                SpirvOp.ConvertFToS,
                                _intType,
                                value),
                            _ => value,
                        };
                        continue;
                    }

                    var raw = LoadV(instruction.Sources[component].Value);
                    values[component] = output.Kind switch
                    {
                        Gen5PixelOutputKind.Uint => raw,
                        Gen5PixelOutputKind.Sint => Bitcast(_intType, raw),
                        _ => Bitcast(_floatType, raw),
                    };
                }

                var vector = _module.AddInstruction(
                    SpirvOp.CompositeConstruct,
                    output.Type,
                    values);
                if (output.Kind == Gen5PixelOutputKind.Float &&
                    PixelExportVgprAddressMatches() &&
                    uint.TryParse(
                        Environment.GetEnvironmentVariable(
                            "PROSPERISMO_FORCE_PIXEL_EXPORT_VGPR_BASE"),
                        out var debugVgprBase))
                {
                    var registerBase = debugVgprBase + export.Target * 4;
                    vector = _module.AddInstruction(
                        SpirvOp.CompositeConstruct,
                        output.Type,
                        Bitcast(_floatType, LoadV(registerBase)),
                        Bitcast(_floatType, LoadV(registerBase + 1)),
                        Bitcast(_floatType, LoadV(registerBase + 2)),
                        Bitcast(_floatType, LoadV(registerBase + 3)));
                }
                if (output.Kind == Gen5PixelOutputKind.Float &&
                    PixelExportVgprAddressMatches() &&
                    uint.TryParse(
                        Environment.GetEnvironmentVariable(
                            "PROSPERISMO_FORCE_PIXEL_EXPORT_PACK_VGPR_BASE"),
                        out var debugPackVgprBase))
                {
                    var registerBase = debugPackVgprBase + export.Target * 4;
                    var debugScale = float.TryParse(
                        Environment.GetEnvironmentVariable(
                            "PROSPERISMO_FORCE_PIXEL_EXPORT_PACK_VGPR_SCALE"),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var requestedDebugScale)
                            ? requestedDebugScale
                            : 1f;
                    uint DebugComponent(uint register) =>
                        _module.AddInstruction(
                            SpirvOp.FMul,
                            _floatType,
                            Bitcast(_floatType, LoadV(register)),
                            Float(debugScale));
                    var lowPair = _module.AddInstruction(
                        SpirvOp.CompositeConstruct,
                        _vec2Type,
                        TruncateFloat32ForPack(DebugComponent(registerBase)),
                        TruncateFloat32ForPack(DebugComponent(registerBase + 1)));
                    var highPair = _module.AddInstruction(
                        SpirvOp.CompositeConstruct,
                        _vec2Type,
                        TruncateFloat32ForPack(DebugComponent(registerBase + 2)),
                        TruncateFloat32ForPack(DebugComponent(registerBase + 3)));
                    var unpackedLow = Ext(62, _vec2Type, Ext(58, _uintType, lowPair));
                    var unpackedHigh = Ext(62, _vec2Type, Ext(58, _uintType, highPair));
                    vector = _module.AddInstruction(
                        SpirvOp.CompositeConstruct,
                        output.Type,
                        _module.AddInstruction(
                            SpirvOp.CompositeExtract,
                            _floatType,
                            unpackedLow,
                            0),
                        _module.AddInstruction(
                            SpirvOp.CompositeExtract,
                            _floatType,
                            unpackedLow,
                            1),
                        _module.AddInstruction(
                            SpirvOp.CompositeExtract,
                            _floatType,
                            unpackedHigh,
                            0),
                        _module.AddInstruction(
                            SpirvOp.CompositeExtract,
                            _floatType,
                            unpackedHigh,
                            1));
                }
                if (_forcePixelMagenta && PixelExportDebugAddressMatches())
                {
                    vector = output.Kind switch
                    {
                        Gen5PixelOutputKind.Float =>
                            _module.AddInstruction(
                                SpirvOp.CompositeConstruct,
                                output.Type,
                                Float(1f),
                                Float(0f),
                                Float(1f),
                                Float(1f)),
                        Gen5PixelOutputKind.Sint =>
                            _module.AddInstruction(
                                SpirvOp.CompositeConstruct,
                                output.Type,
                                Bitcast(_intType, UInt(1)),
                                Bitcast(_intType, UInt(0)),
                                Bitcast(_intType, UInt(1)),
                                Bitcast(_intType, UInt(1))),
                        _ =>
                            _module.AddInstruction(
                                SpirvOp.CompositeConstruct,
                                output.Type,
                                UInt(1),
                                UInt(0),
                                UInt(1),
                                UInt(1)),
                    };
                }
                if (Environment.GetEnvironmentVariable(
                        "PROSPERISMO_FORCE_TITLE_EXPORT_EXEC") == "1" &&
                    _state.Program.Address == 0x0000000500781200ul)
                {
                    Store(_exec, _module.ConstantBool(true));
                    StoreS64(
                        126,
                        _module.Constant64(_ulongType, 1));
                }
                vector = _module.AddInstruction(
                    SpirvOp.Select,
                    output.Type,
                    Load(_boolType, _exec),
                    vector,
                    Load(output.Type, output.Variable));
                Store(output.Variable, vector);
                return true;
            }

            if (_stage != Gen5SpirvStage.Vertex)
            {
                return true;
            }

            uint outputVariable;
            if (export.Target == 12)
            {
                outputVariable = _positionOutput;
            }
            else if (export.Target is >= 13 and < 16)
            {
                EmitVertexPositionAuxiliaryExport(instruction, export);
                return true;
            }
            else if (export.Target is >= 32 and < 64 &&
                     _vertexOutputs.TryGetValue(export.Target - 32, out var parameter))
            {
                outputVariable = parameter;
            }
            else if (export.Target == NggPrimitiveExportTarget)
            {
                if (_nggPrimitiveConnectivity is { } connectivity)
                {
                    ValidateNggPrimitiveExport(instruction, export, connectivity);
                }
                else
                {
                    ReportNggPrimitiveExportDropped(instruction, export);
                }
                return true;
            }
            else
            {
                return true;
            }

            var components = new uint[4];
            for (var component = 0; component < 4; component++)
            {
                components[component] = (export.EnableMask & (1u << component)) != 0
                    ? export.Compressed
                        ? LoadCompressedExportComponent(instruction, component)
                        : Bitcast(
                            _floatType,
                            LoadV(instruction.Sources[component].Value))
                    : Float(component == 3 ? 1f : 0f);
            }

            var outputValue = _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                _vec4Type,
                components);
            if (_state.Program.Address == 0x0000000500780000ul &&
                export.Target is >= 32 and < 36 &&
                Environment.GetEnvironmentVariable(
                    "PROSPERISMO_FORCE_TITLE_VERTEX_OUTPUTS_ONE") == "1")
            {
                outputValue = _module.AddInstruction(
                    SpirvOp.CompositeConstruct,
                    _vec4Type,
                    Float(1f),
                    Float(1f),
                    Float(1f),
                    Float(1f));
            }
            outputValue = _module.AddInstruction(
                SpirvOp.Select,
                _vec4Type,
                Load(_boolType, _exec),
                outputValue,
                Load(_vec4Type, outputVariable));
            Store(outputVariable, outputValue);
            return true;
        }

        /// <summary>
        /// Announces that the NGG primitive-connectivity export was dropped.
        /// </summary>
        /// <remarks>
        /// <c>exp prim</c> (EXP target 20) is how an RDNA2 NGG merged ES/GS
        /// program states the connectivity of the primitive this invocation
        /// owns; it is the *only* way it states it, because NGG has no
        /// canonical shape - <c>AgcCompositor.elf</c> byte offset 0x19821C:
        /// <c>s_sendmsg GS_ALLOC_REQ; exp prim v0 done; exp pos0 done; s_endpgm</c>.
        ///
        /// A Vulkan vertex shader has nowhere to put it: connectivity comes
        /// from the draw's topology and index buffer. That is CORRECT when the
        /// invocation contributes at most one primitive, which is what every
        /// 120 decodable ones found exactly one target-20 export each, always
        /// en=0x1 and done=1, and none of the 122 backward branches across
        /// those programs encloses an export (see NggProgramShape in
        /// Prosperismo.Libs/Agc/NggPrimitiveShader.cs for the full scan). It is
        /// WRONG when an invocation can export a primitive more than once,
        /// which the AGC layer checks per draw with exactly that test. So the
        /// drop is announced rather than silently performed, once per shader,
        /// with the PC and source register needed to find it in a disassembly.
        /// </remarks>
        private void ReportNggPrimitiveExportDropped(
            Gen5ShaderInstruction instruction,
            Gen5ExportControl export)
        {
            var source = instruction.Sources.Count > 0
                ? $"v{instruction.Sources[0].Value}"
                : "none";
            ReportDiagnosticOnce(
                $"ngg-prim-export:0x{_state.Program.Address:X16}",
                "[SPIRV][WARN] " +
                $"shader=0x{_state.Program.Address:X16} " +
                $"pc=0x{instruction.Pc:X4} " +
                "error=ngg-prim-export-dropped target=20 " +
                $"src={source} en=0x{export.EnableMask:X} " +
                $"done={(export.Done ? 1 : 0)} " +
                "detail=NGG primitive connectivity cannot be expressed in the " +
                "vertex stage; the host launch topology is used instead and must " +
                "match the shader's expanded primitive connectivity");
        }

        /// <summary>
        /// Records a target-20 primitive export whose connectivity is carried by
        /// the host input assembly. This is deliberately not a SPIR-V no-op:
        /// the compiler only accepts it when its caller provides the exact
        /// non-indexed launch contract, which is propagated with the compiled
        /// shader and enforced by the Vulkan draw session.
        /// </summary>
        private void ValidateNggPrimitiveExport(
            Gen5ShaderInstruction instruction,
            Gen5ExportControl export,
            Gen5NggPrimitiveConnectivity connectivity)
        {
            if (_stage != Gen5SpirvStage.Vertex)
            {
                throw new InvalidOperationException(
                    "NGG primitive connectivity is only valid for a vertex-stage export");
            }

            if (export.EnableMask != 0x1 || !export.Done)
            {
                throw new InvalidOperationException(
                    $"unsupported NGG primitive export at pc=0x{instruction.Pc:X4}: " +
                    $"en=0x{export.EnableMask:X} done={(export.Done ? 1 : 0)}");
            }

            if (!connectivity.IsValid)
            {
                throw new InvalidOperationException("invalid host NGG primitive connectivity");
            }

            _sawNggPrimitiveExport = true;
        }

        /// <summary>
        /// Routes EXP POS1..POS3 (targets 13..15) to the SPIR-V builtins
        /// PA_CL_VS_OUT_CNTL says each component carries.
        /// <see cref="DeclareVertexPositionAuxiliaryOutputs"/> has already
        /// refused every configuration that cannot be mapped, so every enabled
        /// component reaching this point has a declared destination. The slot is
        /// absent only under the documented
        /// PROSPERISMO_SPIRV_ALLOW_DROPPED_POSITION_EXPORTS opt-out, which already
        /// announced the drop.
        /// </summary>
        private void EmitVertexPositionAuxiliaryExport(
            Gen5ShaderInstruction instruction,
            Gen5ExportControl export)
        {
            if (!_positionSlots.TryGetValue(export.Target, out var components))
            {
                return;
            }

            for (var component = 0; component < 4; component++)
            {
                if ((export.EnableMask & (1u << component)) == 0)
                {
                    continue;
                }

                var semantic = components[component];
                if (semantic.Kind == PositionOutputKind.None)
                {
                    continue;
                }

                var value = export.Compressed
                    ? LoadCompressedExportComponent(instruction, component)
                    : Bitcast(
                        _floatType,
                        LoadV(instruction.Sources[component].Value));
                switch (semantic.Kind)
                {
                    case PositionOutputKind.ClipDistance:
                        StoreExecGuarded(
                            FloatArrayElementPointer(
                                _clipDistanceOutput,
                                semantic.Index),
                            _floatType,
                            value);
                        break;
                    case PositionOutputKind.CullDistance:
                        StoreExecGuarded(
                            FloatArrayElementPointer(
                                _cullDistanceOutput,
                                semantic.Index),
                            _floatType,
                            value);
                        break;
                    case PositionOutputKind.PointSize:
                        StoreExecGuarded(_pointSizeOutput, _floatType, value);
                        break;
                    case PositionOutputKind.Layer:
                        // The guest exports the array slice as a raw dword in a
                        // float-typed export slot, so reinterpret rather than
                        // convert.
                        StoreExecGuarded(
                            _layerOutput,
                            _uintType,
                            Bitcast(_uintType, value));
                        break;
                    case PositionOutputKind.ViewportIndex:
                        StoreExecGuarded(
                            _viewportIndexOutput,
                            _uintType,
                            Bitcast(_uintType, value));
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"unmapped position output {semantic.Kind}");
                }
            }
        }

        private uint FloatArrayElementPointer(uint arrayVariable, uint index) =>
            _module.AddInstruction(
                SpirvOp.AccessChain,
                _module.TypePointer(SpirvStorageClass.Output, _floatType),
                arrayVariable,
                UInt(index));

        /// <summary>
        /// Writes an output only for lanes still in EXEC, matching how the
        /// position and parameter exports above preserve the previous value for
        /// inactive lanes.
        /// </summary>
        private void StoreExecGuarded(uint pointer, uint type, uint value) =>
            Store(
                pointer,
                _module.AddInstruction(
                    SpirvOp.Select,
                    type,
                    Load(_boolType, _exec),
                    value,
                    Load(type, pointer)));

        private bool PixelExportDebugAddressMatches()
        {
            var addressFilter = Environment.GetEnvironmentVariable(
                "PROSPERISMO_FORCE_PIXEL_EXPORT_ADDRESS");
            if (string.IsNullOrWhiteSpace(addressFilter))
            {
                return true;
            }

            var span = addressFilter.AsSpan();
            if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                span = span[2..];
            }

            return ulong.TryParse(
                       span,
                       System.Globalization.NumberStyles.HexNumber,
                       System.Globalization.CultureInfo.InvariantCulture,
                       out var address) &&
                   _state.Program.Address == address;
        }

        private bool PixelImageCaptureAddressMatches()
        {
            var addressFilter = Environment.GetEnvironmentVariable(
                "PROSPERISMO_CAPTURE_PIXEL_IMAGE_ADDRESS");
            if (string.IsNullOrWhiteSpace(addressFilter))
            {
                return false;
            }

            var span = addressFilter.AsSpan();
            if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                span = span[2..];
            }

            return ulong.TryParse(
                       span,
                       System.Globalization.NumberStyles.HexNumber,
                       System.Globalization.CultureInfo.InvariantCulture,
                       out var address) &&
                   _state.Program.Address == address;
        }

        private void CapturePixelVgprs(Gen5ShaderInstruction instruction)
        {
            if (_stage != Gen5SpirvStage.Pixel ||
                !PixelVgprCaptureAddressMatches() ||
                !uint.TryParse(
                    Environment.GetEnvironmentVariable(
                        "PROSPERISMO_CAPTURE_PIXEL_VGPR_PC"),
                    out var capturePc) ||
                instruction.Pc != capturePc)
            {
                return;
            }

            var sourceText = Environment.GetEnvironmentVariable(
                "PROSPERISMO_CAPTURE_PIXEL_VGPR_SOURCES");
            if (string.IsNullOrWhiteSpace(sourceText))
            {
                return;
            }

            var destinationBase = 248u;
            if (uint.TryParse(
                    Environment.GetEnvironmentVariable(
                        "PROSPERISMO_CAPTURE_PIXEL_VGPR_DEST_BASE"),
                    out var requestedDestinationBase))
            {
                destinationBase = requestedDestinationBase;
            }

            var sources = sourceText.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);
            if (sources.Length is 0 or > 4 ||
                destinationBase > 252 ||
                destinationBase + (uint)sources.Length > 256)
            {
                return;
            }

            for (var index = 0; index < sources.Length; index++)
            {
                if (!uint.TryParse(sources[index], out var source) ||
                    source >= 256)
                {
                    return;
                }
            }

            for (var index = 0; index < sources.Length; index++)
            {
                _ = uint.TryParse(sources[index], out var source);
                StoreV(
                    destinationBase + (uint)index,
                    LoadV(source),
                    guardWithExec:
                        Environment.GetEnvironmentVariable(
                            "PROSPERISMO_CAPTURE_PIXEL_VGPR_IGNORE_EXEC") != "1");
            }
        }

        private bool PixelVgprCaptureAddressMatches()
        {
            var addressFilter = Environment.GetEnvironmentVariable(
                "PROSPERISMO_CAPTURE_PIXEL_VGPR_ADDRESS");
            if (string.IsNullOrWhiteSpace(addressFilter))
            {
                return false;
            }

            var span = addressFilter.AsSpan();
            if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                span = span[2..];
            }

            return ulong.TryParse(
                       span,
                       System.Globalization.NumberStyles.HexNumber,
                       System.Globalization.CultureInfo.InvariantCulture,
                       out var address) &&
                   _state.Program.Address == address;
        }

        private void CapturePixelVgprPoints(Gen5ShaderInstruction instruction)
        {
            if (_stage != Gen5SpirvStage.Pixel ||
                !PixelVgprCaptureAddressMatches())
            {
                return;
            }

            var captureText = Environment.GetEnvironmentVariable(
                "PROSPERISMO_CAPTURE_PIXEL_VGPR_POINTS");
            if (string.IsNullOrWhiteSpace(captureText))
            {
                return;
            }

            foreach (var capture in captureText.Split(
                         ',',
                         StringSplitOptions.RemoveEmptyEntries |
                         StringSplitOptions.TrimEntries))
            {
                var fields = capture.Split(':');
                if (fields.Length != 3 ||
                    !uint.TryParse(fields[0], out var pc) ||
                    !uint.TryParse(fields[1], out var source) ||
                    !uint.TryParse(fields[2], out var destination) ||
                    pc != instruction.Pc || source >= 256 || destination >= 256)
                {
                    continue;
                }

                StoreV(
                    destination,
                    LoadV(source),
                    guardWithExec:
                        Environment.GetEnvironmentVariable(
                            "PROSPERISMO_CAPTURE_PIXEL_VGPR_IGNORE_EXEC") != "1");
            }
        }

        private void MarkPixelPath(Gen5ShaderInstruction instruction)
        {
            if (_stage != Gen5SpirvStage.Pixel ||
                !PixelVgprCaptureAddressMatches())
            {
                return;
            }

            var markerText = Environment.GetEnvironmentVariable(
                "PROSPERISMO_MARK_PIXEL_PCS");
            if (string.IsNullOrWhiteSpace(markerText))
            {
                return;
            }

            foreach (var marker in markerText.Split(
                         ',',
                         StringSplitOptions.RemoveEmptyEntries |
                         StringSplitOptions.TrimEntries))
            {
                var separator = marker.IndexOf(':');
                if (separator <= 0 || separator == marker.Length - 1 ||
                    !uint.TryParse(marker.AsSpan(0, separator), out var pc) ||
                    !uint.TryParse(marker.AsSpan(separator + 1), out var register) ||
                    pc != instruction.Pc || register >= 256)
                {
                    continue;
                }

                StoreV(
                    register,
                    Bitcast(_uintType, Float(1)),
                    guardWithExec: false);
            }
        }

        private void CapturePixelExec(Gen5ShaderInstruction instruction)
        {
            if (_stage != Gen5SpirvStage.Pixel ||
                !PixelVgprCaptureAddressMatches())
            {
                return;
            }

            var captureText = Environment.GetEnvironmentVariable(
                "PROSPERISMO_CAPTURE_PIXEL_EXEC_PCS");
            if (string.IsNullOrWhiteSpace(captureText))
            {
                return;
            }

            foreach (var capture in captureText.Split(
                         ',',
                         StringSplitOptions.RemoveEmptyEntries |
                         StringSplitOptions.TrimEntries))
            {
                var separator = capture.IndexOf(':');
                if (separator <= 0 || separator == capture.Length - 1 ||
                    !uint.TryParse(capture.AsSpan(0, separator), out var pc) ||
                    !uint.TryParse(capture.AsSpan(separator + 1), out var register) ||
                    pc != instruction.Pc || register >= 256)
                {
                    continue;
                }

                var value = _module.AddInstruction(
                    SpirvOp.Select,
                    _floatType,
                    Load(_boolType, _exec),
                    Float(1),
                    Float(0));
                StoreV(
                    register,
                    Bitcast(_uintType, value),
                    guardWithExec: false);
            }
        }

        private bool PixelExportVgprAddressMatches()
        {
            var addressFilter = Environment.GetEnvironmentVariable(
                "PROSPERISMO_FORCE_PIXEL_EXPORT_VGPR_ADDRESS");
            if (string.IsNullOrWhiteSpace(addressFilter))
            {
                return PixelExportDebugAddressMatches();
            }

            var span = addressFilter.AsSpan();
            if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                span = span[2..];
            }

            return ulong.TryParse(
                       span,
                       System.Globalization.NumberStyles.HexNumber,
                       System.Globalization.CultureInfo.InvariantCulture,
                       out var address) &&
                   _state.Program.Address == address;
        }

        private uint LoadCompressedExportComponent(
            Gen5ShaderInstruction instruction,
            int component)
        {
            if (TryLoadPackedHalfExportComponent(
                    instruction,
                    component,
                    out var shadowValue))
            {
                return shadowValue;
            }

            var packed = LoadV(instruction.Sources[component >> 1].Value);
            var unpacked = Ext(62, _vec2Type, packed);
            return _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _floatType,
                unpacked,
                (uint)(component & 1));
        }

        private bool TryLoadPackedHalfExportComponent(
            Gen5ShaderInstruction exportInstruction,
            int component,
            out uint value)
        {
            value = 0;
            var packedSource = exportInstruction.Sources[component >> 1];
            var tracePackedExport =
                Environment.GetEnvironmentVariable(
                    "PROSPERISMO_TRACE_PACKED_EXPORT") == "1" &&
                _state.Program.Address == 0x0000000500781200ul;
            if (tracePackedExport)
            {
                Console.Error.WriteLine(
                    $"[AGC][PACKED-EXPORT] exp_pc=0x{exportInstruction.Pc:X} " +
                    $"component={component} source={packedSource.Kind}:" +
                    $"{packedSource.Value}");
                if (component == 0 && exportInstruction.Pc == 0x630)
                {
                    foreach (var decoded in _state.Program.Instructions.Where(
                                 static decoded => decoded.Pc <= 0x640))
                    {
                        Console.Error.WriteLine(
                            $"[AGC][TITLE-IR] 0x{decoded.Pc:X4} " +
                            $"{decoded.Opcode} dst=[" +
                            string.Join(',', decoded.Destinations) +
                            "] src=[" +
                            string.Join(',', decoded.Sources) + "] words=[" +
                            string.Join(',', decoded.Words.Select(static word => $"{word:X8}")) +
                            "] ctrl=" + decoded.Control);
                    }
                }
            }
            if (packedSource.Kind != Gen5OperandKind.VectorRegister)
            {
                if (tracePackedExport)
                {
                    Console.Error.WriteLine(
                        "[AGC][PACKED-EXPORT] rejected: source is not a VGPR");
                }
                return false;
            }

            for (var index = _state.Program.Instructions.Count - 1; index >= 0; index--)
            {
                var candidate = _state.Program.Instructions[index];
                if (candidate.Pc >= exportInstruction.Pc)
                {
                    continue;
                }

                if (exportInstruction.Pc - candidate.Pc > 128)
                {
                    break;
                }

                if (!candidate.Destinations.Any(destination =>
                        destination.Kind == Gen5OperandKind.VectorRegister &&
                        destination.Value == packedSource.Value))
                {
                    continue;
                }

                if (tracePackedExport)
                {
                    Console.Error.WriteLine(
                        $"[AGC][PACKED-EXPORT] nearest_pc=0x{candidate.Pc:X} " +
                        $"opcode={candidate.Opcode} distance=" +
                        $"{exportInstruction.Pc - candidate.Pc}");
                }

                if (candidate.Opcode != "VCvtPkrtzF16F32" ||
                    candidate.Sources.Count < 2)
                {
                    if (tracePackedExport)
                    {
                        Console.Error.WriteLine(
                            "[AGC][PACKED-EXPORT] rejected: nearest writer is " +
                            candidate.Opcode);
                    }
                    return false;
                }

                var packedPointer = PackedHalfPointer(packedSource.Value);
                if (Environment.GetEnvironmentVariable(
                        "PROSPERISMO_FORCE_PACKED_EXPORT_STORE_ONE") == "1" &&
                    _state.Program.Address == 0x0000000500781200ul)
                {
                    Store(
                        packedPointer,
                        _module.AddInstruction(
                            SpirvOp.CompositeConstruct,
                            _vec2Type,
                            Float(1f),
                            Float(1f)));
                }

                var packedPair = Load(
                    _vec2Type,
                    packedPointer);
                value = _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    _floatType,
                    packedPair,
                    (uint)(component & 1));
                if (Environment.GetEnvironmentVariable(
                        "PROSPERISMO_FORCE_PACKED_EXPORT_ONE") == "1")
                {
                    value = Float(1f);
                }
                if (tracePackedExport)
                {
                    Console.Error.WriteLine(
                        "[AGC][PACKED-EXPORT] selected shadow pair");
                }
                return true;
            }

            if (tracePackedExport)
            {
                Console.Error.WriteLine(
                    "[AGC][PACKED-EXPORT] rejected: no nearby writer");
            }
            return false;
        }

        private uint GetPixelOutputType(Gen5PixelOutputKind kind) =>
            kind switch
            {
                Gen5PixelOutputKind.Uint => _uvec4Type,
                Gen5PixelOutputKind.Sint => _module.TypeVector(_intType, 4),
                _ => _vec4Type,
            };

        private uint LoadBufferWord(int binding, uint dwordAddress)
        {
            var inRange = IsBufferWordInRange(binding, dwordAddress);
            var safeAddress = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                inRange,
                dwordAddress,
                UInt(0));
            var value = Load(_uintType, BufferWordPointer(binding, safeAddress));
            return _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                inRange,
                value,
                UInt(0));
        }

        private uint ApplyGuestBufferByteBias(int binding, uint byteAddress)
        {
            var evaluationBinding = binding - _globalBufferBase;
            if ((uint)evaluationBinding >=
                (uint)_evaluation.GlobalMemoryBindings.Count)
            {
                // Runtime SGPR blocks and other synthetic descriptors do not
                // alias guest virtual memory and are always bound at offset 0.
                return byteAddress;
            }

            if (_initialScalarBufferIndex >= 0)
            {
                // Descriptor offsets must satisfy Vulkan's storage-buffer
                // alignment. The presenter therefore rounds the shared guest
                // allocation offset down and packs the discarded low address
                // bits after the 256 initial SGPRs in the per-dispatch runtime
                // block. Keeping this value runtime-stable prevents rotating
                // guest allocations from producing a new multi-megabyte SPIR-V
                // module and Metal pipeline while preserving exact byte access.
                var runtimeByteBias = Load(
                    _uintType,
                    RuntimeBufferBiasPointer(binding));
                return IAdd(byteAddress, runtimeByteBias);
            }

            // The presenter binds the shared allocation at the largest aligned
            // offset not greater than this guest resource's offset. Because the
            // allocation base is aligned to the same power of two, the bytes
            // discarded from the descriptor offset are exactly the low address
            // bits below. Adding them here keeps scalar, MUBUF and GLOBAL paths
            // byte-exact, including atomics and resources that overlap another
            // descriptor at an unaligned guest address.
            var byteBias =
                _evaluation.GlobalMemoryBindings[evaluationBinding].BaseAddress &
                (_storageBufferOffsetAlignment - 1);
            return byteBias == 0
                ? byteAddress
                : IAdd(byteAddress, UInt(checked((uint)byteBias)));
        }

        private void StoreBufferWord(int binding, uint dwordAddress, uint value)
        {
            EmitConditional(
                IsBufferWordInRange(binding, dwordAddress),
                () => Store(BufferWordPointer(binding, dwordAddress), value));
        }

        private uint IsBufferWordInRange(int binding, uint dwordAddress)
        {
            var buffer = _module.AddInstruction(
                SpirvOp.AccessChain,
                _storageBlockPointer,
                _globalBuffers,
                UInt((uint)binding));
            var length = _module.AddInstruction(
                SpirvOp.ArrayLength,
                _uintType,
                buffer,
                0);
            return _module.AddInstruction(
                SpirvOp.ULessThan,
                _boolType,
                dwordAddress,
                length);
        }

        private uint BufferWordPointer(int binding, uint dwordAddress) =>
            _module.AddInstruction(
                SpirvOp.AccessChain,
                _storageUintPointer,
                _globalBuffers,
                UInt((uint)binding),
                UInt(0),
                dwordAddress);

        private uint ScalarPointer(uint register) =>
            _module.AddInstruction(
                SpirvOp.AccessChain,
                _privateUintPointer,
                _scalarRegisters,
                UInt(register));

        private uint RuntimeBufferBiasPointer(int binding) =>
            _module.AddInstruction(
                SpirvOp.AccessChain,
                _privateUintPointer,
                _runtimeBufferBiases,
                UInt(checked((uint)binding)));

        private uint VectorPointer(uint register) =>
            _module.AddInstruction(
                SpirvOp.AccessChain,
                _privateUintPointer,
                _vectorRegisters,
                UInt(register));

        private uint PackedHalfPointer(uint register) =>
            _module.AddInstruction(
                SpirvOp.AccessChain,
                _privateVec2Pointer,
                _packedHalfRegisters,
                UInt(register));

        private uint LoadS(uint register) => Load(_uintType, ScalarPointer(register));

        private uint LoadV(uint register) => Load(_uintType, VectorPointer(register));

        private void StoreS(uint register, uint value)
        {
            Store(ScalarPointer(register), value);
            if (register is 106 or 107)
            {
                Store(_vcc, IsWaveMaskActive(LoadS64(106)));
            }
            else if (register is 126 or 127)
            {
                Store(_exec, IsWaveMaskActive(LoadS64(126)));
            }
        }

        private void StoreV(uint register, uint value, bool guardWithExec = true)
        {
            if (guardWithExec)
            {
                var active = Load(_boolType, _exec);
                var oldValue = LoadV(register);
                value = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    active,
                    value,
                    oldValue);
            }

            Store(VectorPointer(register), value);
        }

        private void StorePackedHalf(uint register, uint value)
        {
            var active = Load(_boolType, _exec);
            if (Environment.GetEnvironmentVariable(
                    "PROSPERISMO_FORCE_PACKED_STORE_EXEC_VALUES") == "1" &&
                _state.Program.Address == 0x0000000500781200ul)
            {
                var activePair = _module.AddInstruction(
                    SpirvOp.CompositeConstruct,
                    _vec2Type,
                    Float(1f),
                    Float(1f));
                var inactivePair = _module.AddInstruction(
                    SpirvOp.CompositeConstruct,
                    _vec2Type,
                    Float(0.5f),
                    Float(0.5f));
                value = _module.AddInstruction(
                    SpirvOp.Select,
                    _vec2Type,
                    active,
                    activePair,
                    inactivePair);
                Store(PackedHalfPointer(register), value);
                return;
            }

            value = _module.AddInstruction(
                SpirvOp.Select,
                _vec2Type,
                active,
                value,
                Load(_vec2Type, PackedHalfPointer(register)));
            Store(PackedHalfPointer(register), value);
        }

        private uint Load(uint type, uint pointer)
        {
            if (pointer == 0)
            {
                throw new InvalidOperationException(
                    "SPIR-V generator attempted OpLoad from id 0.");
            }

            return _module.AddInstruction(SpirvOp.Load, type, pointer);
        }

        private void Store(uint pointer, uint value) =>
            _module.AddStatement(SpirvOp.Store, pointer, value);

        private uint UInt(uint value) => _module.Constant(_uintType, value);

        private uint Float(float value) => _module.ConstantFloat(_floatType, value);

        private uint Bitcast(uint type, uint value) =>
            _module.AddInstruction(SpirvOp.Bitcast, type, value);

        private uint IAdd(uint left, uint right) =>
            _module.AddInstruction(SpirvOp.IAdd, _uintType, left, right);

        private uint ShiftLeftLogical(uint left, uint right) =>
            _module.AddInstruction(
                SpirvOp.ShiftLeftLogical,
                _uintType,
                left,
                BitwiseAnd(right, UInt(31)));

        private uint ShiftRightLogical(uint left, uint right) =>
            _module.AddInstruction(
                SpirvOp.ShiftRightLogical,
                _uintType,
                left,
                BitwiseAnd(right, UInt(31)));

        private uint ShiftRightArithmetic(uint left, uint right) =>
            Bitcast(
                _uintType,
                _module.AddInstruction(
                    SpirvOp.ShiftRightArithmetic,
                    _intType,
                    Bitcast(_intType, left),
                    BitwiseAnd(right, UInt(31))));

        private uint ShiftLeftLogical64(uint left, uint right) =>
            _module.AddInstruction(
                SpirvOp.ShiftLeftLogical,
                _ulongType,
                left,
                BitwiseAnd64(right, _module.Constant64(_ulongType, 63)));

        private uint ShiftRightLogical64(uint left, uint right) =>
            _module.AddInstruction(
                SpirvOp.ShiftRightLogical,
                _ulongType,
                left,
                BitwiseAnd64(right, _module.Constant64(_ulongType, 63)));

        private uint BitwiseAnd(uint left, uint right) =>
            _module.AddInstruction(SpirvOp.BitwiseAnd, _uintType, left, right);

        private uint BitwiseAnd64(uint left, uint right) =>
            _module.AddInstruction(SpirvOp.BitwiseAnd, _ulongType, left, right);

        private uint BitwiseOr64(uint left, uint right) =>
            _module.AddInstruction(SpirvOp.BitwiseOr, _ulongType, left, right);

        private uint BitwiseOr(uint left, uint right) =>
            _module.AddInstruction(SpirvOp.BitwiseOr, _uintType, left, right);

        private uint BitwiseXor(uint left, uint right) =>
            _module.AddInstruction(SpirvOp.BitwiseXor, _uintType, left, right);

        private uint LogicalNot(uint value) =>
            _module.AddInstruction(SpirvOp.LogicalNot, _boolType, value);

        private uint SubgroupAny(uint condition) =>
            _subgroupInvocationIdInput == 0
                ? condition
                : _emulateWave64
                    ? IsNotZero64(BooleanToWaveMask(condition))
                : _waveLaneCount == 32 && _hostSubgroupSize > 32
                    ? IsNotZero64(BooleanToWaveMask(condition))
                : _module.AddInstruction(
                    SpirvOp.GroupNonUniformAny,
                    _boolType,
                    UInt(3),
                    condition);

        /// <summary>
        /// How many host subgroup lanes the emitted module treats as one RDNA
        /// wave32. Never more than the host subgroup actually has, and never
        /// more than a guest wave holds.
        /// </summary>
        private uint ModelledWaveLaneCount =>
            Math.Min(RdnaWaveLaneCount, _hostSubgroupSize);

        /// <summary>
        /// AND mask that turns a host SubgroupLocalInvocationId into a guest
        /// wave32 lane id. With the default 32-lane host subgroup this is the
        /// historical 31.
        /// </summary>
        private uint GuestWaveLaneMask => ModelledWaveLaneCount - 1;

        private uint GuestWaveLane()
        {
            if (_waveLaneCount == 64 && _localInvocationIndexInput != 0)
            {
                return BitwiseAnd(
                    Load(_uintType, _localInvocationIndexInput),
                    UInt(63));
            }

            if (_subgroupInvocationIdInput != 0)
            {
                return BitwiseAnd(
                    Load(_uintType, _subgroupInvocationIdInput),
                    UInt(GuestWaveLaneMask));
            }

            // Graphics stages without subgroup support have one logical lane;
            // they must not emit OpLoad for absent SPIR-V input ID zero.
            return UInt(0);
        }

        private uint CurrentLaneBit()
        {
            if (_subgroupInvocationIdInput == 0)
            {
                return _module.Constant64(_ulongType, 1);
            }

            var maskedLane = GuestWaveLane();
            var shifted = ShiftLeftLogical64(
                _module.Constant64(_ulongType, 1),
                _module.AddInstruction(
                    SpirvOp.UConvert,
                    _ulongType,
                    maskedLane));
            return _emulateWave64
                ? shifted
                : _module.AddInstruction(
                    SpirvOp.Select,
                    _ulongType,
                    IsCurrentLaneInRdnaWave(),
                    shifted,
                    _module.Constant64(_ulongType, 0));
        }

        private uint IsCurrentLaneInRdnaWave() =>
            _waveLaneCount == 32 && _hostSubgroupSize >= 32
                ? _module.ConstantBool(true)
                : _module.AddInstruction(
                    SpirvOp.ULessThan,
                    _boolType,
                    Load(_uintType, _subgroupInvocationIdInput),
                    UInt(ModelledWaveLaneCount));

        private uint BooleanToLaneMask(uint condition) =>
            _module.AddInstruction(
                SpirvOp.Select,
                _ulongType,
                condition,
                CurrentLaneBit(),
                _module.Constant64(_ulongType, 0));

        private uint BooleanToWaveMask(uint condition)
        {
            if (_subgroupInvocationIdInput == 0)
            {
                return BooleanToLaneMask(condition);
            }

            var ballot = _module.AddInstruction(
                SpirvOp.GroupNonUniformBallot,
                _uvec4Type,
                UInt(3),
                condition);
            var low = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _uintType,
                ballot,
                0);
            if (_waveLaneCount == 32 && _hostSubgroupSize > 32)
            {
                var high = _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    _uintType,
                    ballot,
                    1);
                low = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    _module.AddInstruction(
                        SpirvOp.UGreaterThanEqual,
                        _boolType,
                        Load(_uintType, _subgroupInvocationIdInput),
                        UInt(32)),
                    high,
                    low);
            }
            if (_emulateWave64)
            {
                var high = _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    _uintType,
                    ballot,
                    1);
                var subgroupLane =
                    Load(_uintType, _subgroupInvocationIdInput);
                var firstLane = _module.AddInstruction(
                    SpirvOp.IEqual,
                    _boolType,
                    subgroupLane,
                    UInt(0));
                var half = ShiftRightLogical(GuestWaveLane(), UInt(5));
                EmitConditional(firstLane, () =>
                {
                    Store(WaveMaskScratchPointer(half), low);

                    var nativeWave64 = _module.AddInstruction(
                        SpirvOp.UGreaterThanEqual,
                        _boolType,
                        Load(_uintType, _subgroupSizeInput),
                        UInt(64));
                    EmitConditional(nativeWave64, () =>
                    {
                        Store(WaveMaskScratchPointer(UInt(1)), high);
                    });
                });
                EmitWave64Barrier();
                var lowMask = Load(
                    _uintType,
                    WaveMaskScratchPointer(UInt(0)));
                var highMask = Load(
                    _uintType,
                    WaveMaskScratchPointer(UInt(1)));
                var combined = BitwiseOr64(
                    _module.AddInstruction(
                        SpirvOp.UConvert,
                        _ulongType,
                        lowMask),
                    ShiftLeftLogical64(
                        _module.AddInstruction(
                            SpirvOp.UConvert,
                            _ulongType,
                            highMask),
                        _module.Constant64(_ulongType, 32)));
                EmitWave64Barrier();
                return combined;
            }

            var widened = _module.AddInstruction(SpirvOp.UConvert, _ulongType, low);
            if (_waveLaneCount != 64)
            {
                return widened;
            }

            return _module.AddInstruction(
                SpirvOp.Select,
                _ulongType,
                _module.AddInstruction(
                    SpirvOp.UGreaterThanEqual,
                    _boolType,
                    GuestWaveLane(),
                    UInt(32)),
                ShiftLeftLogical64(
                    widened,
                    _module.Constant64(_ulongType, 32)),
                widened);
        }

        private uint WaveMaskScratchPointer(uint index) =>
            _module.AddInstruction(
                SpirvOp.AccessChain,
                _waveMaskScratchElementPointer,
                _waveScratchInLds ? _lds : _waveMaskScratch,
                _waveScratchInLds ? IAdd(UInt(LdsDwordCount - 3), index) : index);

        private uint WaveBroadcastScratchPointer() =>
            _waveScratchInLds
                ? _module.AddInstruction(
                    SpirvOp.AccessChain,
                    _ldsElementPointer,
                    _lds,
                    UInt(LdsDwordCount - 1))
                : _waveBroadcastScratch;

        private void EmitWave64Barrier()
        {
            var workgroup = UInt(2);
            _module.AddStatement(
                SpirvOp.ControlBarrier,
                workgroup,
                workgroup,
                UInt(WorkgroupBarrierMemorySemantics));
        }

        // A wave-mask SGPR (VCC/EXEC) consumed as a per-lane predicate. The
        // condition of VCndmask, a VCC/EXEC branch, or the derived _vcc/_exec
        // bool must be tested at the CURRENT lane's bit, exactly as the
        // hardware does, not as "the 64-bit value is non-zero". The two coincide
        // for comparison results (only the lane's own bit is ever set), so the
        // single-lane path historically used a cheaper whole-word non-zero test.
        // But bitwise-complement wave-mask idioms (S_NOT/S_ORN2/S_ANDN2/S_NAND/
        // S_NOR on a 64-bit mask) set the unused upper 63 bits; a whole-word test
        // then reports "lane active" even when this lane's bit is clear. Unity's
        // PostProcessing NaN killer does exactly this (`anyNaN | ~allFinite`),
        // which made every valid pixel read as NaN and get replaced with 0,
        // zeroing the whole scene before tonemap. Extract the lane bit always.
        private uint IsWaveMaskActive(uint mask) =>
            IsCurrentLaneSet(mask);

        private uint IsCurrentLaneSet(uint mask) =>
            IsNotZero64(
                _module.AddInstruction(
                    SpirvOp.BitwiseAnd,
                    _ulongType,
                    mask,
                    CurrentLaneBit()));

        // EXTRACTED: ===== 38.pdf p9 ===== makes thread masks one dword in
        // wave32 and an aligned SGPR pair only in wave64. ===== 01.pdf p2 =====
        // makes wave32 the default for every stage except pixel.
        private void StoreWaveMask(uint register, uint condition)
        {
            var mask = BooleanToWaveMask(condition);
            if (_waveLaneCount == 64)
            {
                StoreS64(register, mask);
                return;
            }

            StoreS(register, _module.AddInstruction(SpirvOp.UConvert, _uintType, mask));
        }

        // Widen a wave mask to whole four-lane quads and return the current
        // lane's resulting bit. The full mask is also used for the SCC result.
        private uint WholeQuadModeExpand(uint sourceLaneSet, out uint expandedFullMask)
        {
            var fullMask = BooleanToWaveMask(sourceLaneSet);
            expandedFullMask = ExpandWholeQuads(fullMask);
            return BooleanToLaneMask(IsCurrentLaneSet(expandedFullMask));
        }

        private uint ExpandWholeQuads(uint mask)
        {
            var quadAny = BitwiseAnd64(
                BitwiseOr64(
                    mask,
                    BitwiseOr64(
                        ShiftRightLogical64(mask, _module.Constant64(_ulongType, 1)),
                        BitwiseOr64(
                            ShiftRightLogical64(mask, _module.Constant64(_ulongType, 2)),
                            ShiftRightLogical64(mask, _module.Constant64(_ulongType, 3))))),
                _module.Constant64(_ulongType, 0x1111_1111_1111_1111UL));
            return _module.AddInstruction(
                SpirvOp.IMul,
                _ulongType,
                quadAny,
                _module.Constant64(_ulongType, 0xFUL));
        }

        private void EmitExecConditional(Action emit)
        {
            var active = Load(_boolType, _exec);
            EmitConditional(active, emit);
        }

        private void EmitConditional(uint condition, Action emit)
        {
            var activeLabel = _module.AllocateId();
            var mergeLabel = _module.AllocateId();
            _module.AddStatement(SpirvOp.SelectionMerge, mergeLabel, 0);
            _module.AddStatement(
                SpirvOp.BranchConditional,
                condition,
                activeLabel,
                mergeLabel);
            _module.AddLabel(activeLabel);
            emit();
            _module.AddStatement(SpirvOp.Branch, mergeLabel);
            _module.AddLabel(mergeLabel);
        }

        private bool UsesLds() =>
            _state.Program.Instructions.Any(instruction =>
                instruction.Control is Gen5DataShareControl &&
                // ds_permute/ds_bpermute/ds_swizzle borrow the LDS crossbar but
                // address no LDS storage, so they must not force an allocation.
                instruction.Opcode is not
                    ("DsPermuteB32" or "DsBpermuteB32" or "DsSwizzleB32"));

        private bool UsesSubgroupShuffle() =>
            _state.Program.Instructions.Any(instruction =>
                instruction.Control is Gen5DppControl or Gen5Dpp8Control ||
                instruction.Opcode is "VPermlane16B32" or "VPermlanex16B32" or "VReadlaneB32" ||
                // The LDS crossbar shuffles lower to OpGroupNonUniformShuffle
                // and therefore need the same capability as v_permlane.
                instruction.Opcode is
                    "DsPermuteB32" or "DsBpermuteB32" or "DsSwizzleB32");

        private bool UsesSubgroupBroadcast() =>
            _state.Program.Instructions.Any(instruction =>
                instruction.Opcode == "VReadfirstlaneB32");

        private bool UsesWaveControl() =>
            _state.Program.Instructions.Any(instruction =>
                instruction.Opcode.Contains("Saveexec", StringComparison.Ordinal) ||
                instruction.Opcode.StartsWith("SCbranchExec", StringComparison.Ordinal) ||
                instruction.Opcode.StartsWith("SCbranchVcc", StringComparison.Ordinal) ||
                instruction.Opcode.StartsWith("VCmpx", StringComparison.Ordinal) ||
                instruction.Sources.Any(IsWaveMaskOperand) ||
                instruction.Destinations.Any(IsWaveMaskOperand));

        /// <summary>
        /// True when the guest program contains at least one construct whose
        /// result depends on how many lanes a wave has: a cross-lane data
        /// movement, a wave-mask materialisation, or a lane-id derivation.
        /// </summary>
        /// <remarks>
        /// This is the stage-independent half of
        /// <see cref="UsesSubgroupOperations"/>. It exists so a GRAPHICS stage
        /// can be asked the same question, which
        /// <see cref="UsesSubgroupOperations"/> cannot answer: that predicate
        /// is false for every vertex and pixel shader by construction, because
        /// graphics stages are compiled without a subgroup invocation id at
        /// all. A program for which this returns false translates to the same
        /// module whether the guest wave is 32 or 64 lanes wide, so the wave
        /// width is genuinely irrelevant to it and no claim about the width has
        /// to be believed.
        /// </remarks>
        private bool ObservesWaveWidth() =>
            UsesSubgroupShuffle() ||
            UsesSubgroupBroadcast() ||
            UsesWaveControl() ||
            _state.Program.Instructions.Any(static instruction =>
                instruction.Opcode is "VMbcntLoU32B32" or "VMbcntHiU32B32");

        private bool UsesSubgroupOperations() =>
            // Pixel programs can contain real DPP/readlane instructions too.
            // libScePsm's AreaFocus and LineFocus both do: compiling graphics
            // stages without a subgroup lane id emitted shuffle operations
            // with lane zero and omitted their capabilities, producing invalid
            // SPIR-V and the wrong per-quad values. Vertex shaders remain on
            // the conservative single-invocation path until a shipped vertex
            // program proves a cross-lane contract and its stage plumbing.
            _stage is Gen5SpirvStage.Compute or Gen5SpirvStage.Pixel &&
            ObservesWaveWidth();

        /// <summary>
        /// True when the emitted module's lane maths actually models a guest
        /// wave of <see cref="_waveLaneCount"/> lanes.
        /// </summary>
        /// <remarks>
        /// wave32 is modelled one host subgroup lane per guest lane. wave64 is
        /// modelled only by the LDS-scratch bridge in
        /// <see cref="DeclareWave64Scratch"/>, and that bridge rendezvouses
        /// through WORKGROUP memory and a workgroup <c>OpControlBarrier</c>, so
        /// it is only sound when the workgroup IS the wave - which is why
        /// <see cref="_emulateWave64"/> demands exactly 64 threads. Any other
        /// wave64 shape falls outside what this module can express.
        /// </remarks>
        private bool WaveWidthIsModelled =>
            _waveLaneCount != 64 || _emulateWave64;

        /// <summary>
        /// Refuses, before a single instruction is emitted, the compiles whose
        /// declared guest wave width the emitted module provably does not
        /// model AND whose program can tell the difference.
        /// </summary>
        /// <remarks>
        /// The point is that the wave width stops being an unstated assumption.
        /// Two reachable cases:
        ///
        /// COMPUTE, wave64, workgroup != 64 threads. Reachable from the guest:
        /// AgcExports.cs:10037 makes every dispatch that leaves
        /// COMPUTE_DISPATCH_INITIATOR.CS_W32_EN clear a wave64 dispatch, and
        /// AgcExports.cs:10281 accepts workgroups up to 1024 threads, so the
        /// combination is not merely possible, it is the default for any
        /// wave64 shader whose workgroup is not exactly one wave. In that state
        /// <see cref="_waveLaneCount"/> is 64 while
        /// <see cref="_emulateWave64"/> is false, and
        /// <see cref="GuestWaveLane"/> still numbers lanes 0..63 from
        /// LocalInvocationIndex while every OpGroupNonUniform* the module emits
        /// is scoped to a 32-lane host subgroup. Masks then cover half the
        /// wave and shuffle ids can exceed the subgroup size, which SPIR-V
        /// leaves undefined. The tier-1 opcodes below cannot survive that at
        /// all and are refused; the rest is announced by
        /// <see cref="ReportUnmodelledWave64Approximation"/> rather than
        /// refused, because refusing every wave64 dispatch that merely touches
        /// VCC would drop most of the compute in a title.
        ///
        /// GRAPHICS, wave64. NOT reachable today - no production caller passes
        /// a wave width to the graphics entry points, because nothing decodes
        /// one (see <see cref="_waveLaneCount"/>). Pixel wave32 programs that
        /// use DPP/readlane do receive a subgroup lane id; the check exists so
        /// that when a
        /// caller does start passing one it fails loudly here instead of
        /// silently rendering through a single-lane model.
        /// </remarks>
        private bool ValidateWaveWidthIsModelled(out string error)
        {
            error = string.Empty;
            if (WaveWidthIsModelled)
            {
                return true;
            }

            if (!ObservesWaveWidth())
            {
                // Nothing in the program can tell 32 from 64, so the claim does
                // not have to be believed and the module is correct either way.
                ReportDiagnosticOnce(
                    $"wave64-unobserved:{_stage}",
                    $"[SPIRV][WARN] program=0x{_state.Program.Address:X16} " +
                    $"stage={_stage} declares a 64-lane guest wave that this " +
                    "translator does not model, but the program contains no " +
                    "cross-lane op, wave-mask operand or lane-id derivation, " +
                    "so the emitted module is width-independent.");
                return true;
            }

            if (_stage != Gen5SpirvStage.Compute)
            {
                ReportDiagnosticOnce(
                    $"wave64-graphics:{_stage}",
                    $"[SPIRV][ERROR] program=0x{_state.Program.Address:X16} " +
                    $"stage={_stage} was compiled for a 64-lane guest wave. " +
                    "Graphics stages are emitted without any subgroup " +
                    "invocation id, so their EXEC/VCC masks, lane ids and " +
                    "cross-lane reads model a single-lane wave; a 64-lane " +
                    "program would render wrong rather than fail. Translation " +
                    "fails instead. Wiring a real graphics wave width in " +
                    "requires a wave64 bridge for vertex/pixel first.");
                error =
                    $"stage {_stage} cannot model a {_waveLaneCount}-lane guest wave";
                return false;
            }

            ReportUnmodelledWave64Approximation();
            return true;
        }

        /// <summary>
        /// Announces, once per program, that a wave64 compute shader is being
        /// translated with only half of each guest wave visible to any one
        /// invocation.
        /// </summary>
        private void ReportUnmodelledWave64Approximation() =>
            ReportDiagnosticOnce(
                $"wave64-unmodelled:{_state.Program.Address:X16}",
                $"[SPIRV][WARN] program=0x{_state.Program.Address:X16} " +
                "declares a 64-lane guest wave but its workgroup is " +
                $"{_localSizeX}x{_localSizeY}x{_localSizeZ} = " +
                $"{(ulong)_localSizeX * _localSizeY * _localSizeZ} threads, " +
                "so the wave64 bridge (which rendezvouses through workgroup " +
                "memory and needs the workgroup to BE the wave) is off. " +
                "Ballots, v_readfirstlane, v_mbcnt and EXEC/VCC materialised " +
                "as data therefore cover 32 of the 64 guest lanes. Cross-lane " +
                "ops that cannot be approximated at all are refused " +
                "separately.");

        private static bool IsWaveMaskOperand(Gen5Operand operand) =>
            operand.Kind == Gen5OperandKind.ScalarRegister &&
            operand.Value is 106 or 107 or 126 or 127;

        private static bool TryGetVectorDestination(
            Gen5ShaderInstruction instruction,
            out uint destination)
        {
            if (instruction.Destinations.Count != 0 &&
                instruction.Destinations[0].Kind == Gen5OperandKind.VectorRegister)
            {
                destination = instruction.Destinations[0].Value;
                return true;
            }

            destination = 0;
            return false;
        }

        private static bool IsBranch(string opcode) =>
            opcode == "SBranch" ||
            opcode.StartsWith("SCbranch", StringComparison.Ordinal);

        private static bool TryGetBranchTargetPc(
            Gen5ShaderInstruction instruction,
            out uint targetPc)
        {
            targetPc = 0;
            if (instruction.Encoding != Gen5ShaderEncoding.Sopp ||
                instruction.Words.Count == 0)
            {
                return false;
            }

            var offset = unchecked((short)(instruction.Words[0] & 0xFFFF));
            var nextPc = (long)instruction.Pc +
                (instruction.Words.Count * sizeof(uint));
            var target = nextPc + (offset * sizeof(uint));
            if (target < 0 || target > uint.MaxValue)
            {
                return false;
            }

            targetPc = (uint)target;
            return true;
        }

        private static IReadOnlyList<ShaderBlock> BuildBasicBlocks(
            IReadOnlyList<Gen5ShaderInstruction> instructions)
        {
            if (instructions.Count == 0)
            {
                return [];
            }

            var leaders = new SortedSet<uint> { instructions[0].Pc };
            for (var index = 0; index < instructions.Count; index++)
            {
                var instruction = instructions[index];
                if (IsBranch(instruction.Opcode) &&
                    TryGetBranchTargetPc(instruction, out var targetPc))
                {
                    leaders.Add(targetPc);
                }

                if ((IsBranch(instruction.Opcode) || instruction.Opcode == "SEndpgm") &&
                    index + 1 < instructions.Count)
                {
                    leaders.Add(instructions[index + 1].Pc);
                }
            }

            var starts = leaders
                .Where(pc => instructions.Any(instruction => instruction.Pc == pc))
                .ToArray();
            var blocks = new List<ShaderBlock>(starts.Length);
            for (var index = 0; index < starts.Length; index++)
            {
                var startIndex = FindInstructionIndex(instructions, starts[index]);
                var endIndex = index + 1 < starts.Length
                    ? FindInstructionIndex(instructions, starts[index + 1])
                    : instructions.Count;
                if (startIndex >= 0 && endIndex > startIndex)
                {
                    blocks.Add(new ShaderBlock(starts[index], startIndex, endIndex));
                }
            }

            return blocks;
        }

        private void BuildScalarDefinitionInfo(
            IReadOnlyList<ShaderBlock> blocks,
            IReadOnlyList<Gen5ShaderInstruction> instructions)
        {
            var predecessors = new HashSet<int>[blocks.Count];
            for (var index = 0; index < blocks.Count; index++)
            {
                predecessors[index] = [];
            }

            void AddEdge(int source, int destination)
            {
                if (destination < 0 || destination >= blocks.Count)
                {
                    return;
                }

                predecessors[destination].Add(source);
            }

            for (var blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
            {
                var block = blocks[blockIndex];
                var terminator = instructions[block.EndIndex - 1];
                var hasFallthrough = blockIndex + 1 < blocks.Count;
                if (terminator.Opcode == "SEndpgm")
                {
                    continue;
                }

                if (terminator.Opcode == "SBranch")
                {
                    if (TryGetBranchTargetPc(terminator, out var targetPc) &&
                        TryFindBlock(blocks, targetPc, out var targetBlock))
                    {
                        AddEdge(blockIndex, targetBlock);
                    }

                    continue;
                }

                if (terminator.Opcode.StartsWith("SCbranch", StringComparison.Ordinal))
                {
                    if (TryGetBranchTargetPc(terminator, out var targetPc) &&
                        TryFindBlock(blocks, targetPc, out var targetBlock))
                    {
                        AddEdge(blockIndex, targetBlock);
                    }

                    if (hasFallthrough)
                    {
                        AddEdge(blockIndex, blockIndex + 1);
                    }

                    continue;
                }

                if (hasFallthrough)
                {
                    AddEdge(blockIndex, blockIndex + 1);
                }
            }

            var blockInputs = new long[blocks.Count][];
            var blockOutputs = new long[blocks.Count][];
            var hasOutput = new bool[blocks.Count];
            var initialDefinitions = Enumerable.Repeat(
                InitialScalarDefinition,
                ScalarRegisterCount).ToArray();

            static void MergeDefinitions(
                long[] destination,
                long[] source,
                ref bool hasInput)
            {
                if (!hasInput)
                {
                    Array.Copy(source, destination, ScalarRegisterCount);
                    hasInput = true;
                    return;
                }

                for (var register = 0; register < ScalarRegisterCount; register++)
                {
                    if (destination[register] != source[register])
                    {
                        destination[register] = ConflictingScalarDefinition;
                    }
                }
            }

            static void ApplyScalarDefinitions(
                long[] definitions,
                ShaderBlock block,
                IReadOnlyList<Gen5ShaderInstruction> blockInstructions)
            {
                for (var instructionIndex = block.StartIndex;
                     instructionIndex < block.EndIndex;
                     instructionIndex++)
                {
                    var instruction = blockInstructions[instructionIndex];
                    foreach (var destination in instruction.Destinations)
                    {
                        if (destination.Kind == Gen5OperandKind.ScalarRegister &&
                            destination.Value < ScalarRegisterCount)
                        {
                            definitions[destination.Value] = instruction.Pc + 1L;
                        }
                    }
                }
            }

            var changed = true;
            while (changed)
            {
                changed = false;
                for (var blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
                {
                    var input = Enumerable.Repeat(
                        UnreachableScalarDefinition,
                        ScalarRegisterCount).ToArray();
                    var hasInput = false;
                    if (blockIndex == 0)
                    {
                        MergeDefinitions(input, initialDefinitions, ref hasInput);
                    }

                    foreach (var predecessor in predecessors[blockIndex])
                    {
                        if (hasOutput[predecessor])
                        {
                            MergeDefinitions(
                                input,
                                blockOutputs[predecessor],
                                ref hasInput);
                        }
                    }

                    if (!hasInput)
                    {
                        continue;
                    }

                    var output = (long[])input.Clone();
                    ApplyScalarDefinitions(output, blocks[blockIndex], instructions);
                    if (!hasOutput[blockIndex] ||
                        !blockInputs[blockIndex].AsSpan().SequenceEqual(input) ||
                        !blockOutputs[blockIndex].AsSpan().SequenceEqual(output))
                    {
                        blockInputs[blockIndex] = input;
                        blockOutputs[blockIndex] = output;
                        hasOutput[blockIndex] = true;
                        changed = true;
                    }
                }
            }

            _scalarDefinitionsBeforePc.Clear();
            for (var blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
            {
                if (!hasOutput[blockIndex])
                {
                    continue;
                }

                var definitions = (long[])blockInputs[blockIndex].Clone();
                var block = blocks[blockIndex];
                for (var instructionIndex = block.StartIndex;
                     instructionIndex < block.EndIndex;
                     instructionIndex++)
                {
                    var instruction = instructions[instructionIndex];
                    if (instruction.Control is Gen5ImageControl or
                            Gen5ScalarMemoryControl or
                            Gen5GlobalMemoryControl or
                            Gen5BufferMemoryControl)
                    {
                        _scalarDefinitionsBeforePc[instruction.Pc] =
                            (long[])definitions.Clone();
                    }
                    foreach (var destination in instruction.Destinations)
                    {
                        if (destination.Kind == Gen5OperandKind.ScalarRegister &&
                            destination.Value < ScalarRegisterCount)
                        {
                            definitions[destination.Value] = instruction.Pc + 1L;
                        }
                    }
                }
            }
        }

        private static int FindInstructionIndex(
            IReadOnlyList<Gen5ShaderInstruction> instructions,
            uint pc)
        {
            for (var index = 0; index < instructions.Count; index++)
            {
                if (instructions[index].Pc == pc)
                {
                    return index;
                }
            }

            return -1;
        }

        private static bool TryFindBlock(
            IReadOnlyList<ShaderBlock> blocks,
            uint pc,
            out int block)
        {
            for (var index = 0; index < blocks.Count; index++)
            {
                if (blocks[index].StartPc == pc)
                {
                    block = index;
                    return true;
                }
            }

            block = -1;
            return false;
        }

        private readonly record struct ShaderBlock(
            uint StartPc,
            int StartIndex,
            int EndIndex);
    }
}
