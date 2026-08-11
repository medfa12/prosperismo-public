// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

// Executes the Prosperismo-emitted "exec" conformance shader on a real Vulkan
// device and compares the buffer results against CPU-computed expected values.
//
// The shader (exec-cs.spv, produced by Prosperismo.Tools.ShaderDump) was
// translated by Prosperismo from hand-assembled Gen5 instruction words and stores
// results to guestBuffers[0]:
//   [0] v_fmac_f32   -> fma(1.5f, 2.25f, 10.0f)
//   [1] v_mul_hi_i32 -> high 32 bits of (int)0x7FFFFFFF * (int)0x00010003
//   [2] v_mul_lo_i32 -> low  32 bits of the same product
//   [3] store attempted with EXEC=0 -> must NOT land (sentinel remains)
//   [4] store after EXEC restored   -> 1.5f (0x3FC00000)
//   [5] v_pk_fma_f16 fma(2.5h, 21024h,  7.496e-5h) -> 0x7A6B packed; the exact
//       sum sits just above an f16 midpoint, so a double-rounded f32
//       multiply-add would give 0x7A6A instead
//   [6] the same fma with the addend negated -> 0x7A6A packed (just below the
//       same midpoint), pinning the opposite rounding direction
//   [7] the pinned fma with the clamp modifier -> 0x3C00 packed (both lanes
//       exceed 1.0, so each saturates to 1.0)
// Every other word of the buffer must still hold the sentinel afterwards.
//
// Creating the compute pipeline doubles as a driver-acceptance check for the
// emitted SPIR-V; the dispatch then verifies the arithmetic numerically.
//
// Usage:
//   Prosperismo.Tools.GpuConformance <path-to-exec-cs.spv>
//   Prosperismo.Tools.GpuConformance --ps5-particle <path-to-particle_c.spv>

using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace Prosperismo.Libs.Presentation;

public static class GpuConformanceRunner
{
    private static readonly object ParticleComputeGate = new();

    internal static uint ParticleDispatchGroupCount(uint particleCount) =>
        Math.Max(1u, (particleCount / 64u) + (particleCount % 64u == 0 ? 0u : 1u));

    /// <summary>
    /// A serialized pattern may address a bank that has no constructed native
    /// particle system. Those partial blocks retain a zero maxParticleId and
    /// must be skipped exactly like the null pointer in the native eight-system
    /// walk; dispatching them aliases every invocation onto record zero.
    /// </summary>
    internal static bool IsActiveSmallParticleResource(ReadOnlySpan<byte> resource)
    {
        if (resource.Length < Ps5NativeParticleComputeRequest.ResourceByteCount)
        {
            return false;
        }

        var particleCount = System.Buffers.Binary.BinaryPrimitives
            .ReadUInt32LittleEndian(resource[0x28..]);
        var maxParticleId = System.Buffers.Binary.BinaryPrimitives
            .ReadUInt32LittleEndian(resource[0x2C..]);
        var indexStride = System.Buffers.Binary.BinaryPrimitives
            .ReadUInt32LittleEndian(resource[0x34..]);
        return particleCount > 0 && maxParticleId > 0 && indexStride > 0;
    }

    internal static void ApplySmallDrawHistory(
        Span<byte> properties,
        ReadOnlySpan<byte> particleIds,
        uint particleCount)
    {
        const int propertyStride = 0x44;
        const int currentLifeOffset = 0x38;
        const int priorLifeOffset = 0x40;
        if (properties.Length != Ps5NativeParticleComputeRequest.ParticlePropertyByteCount ||
            particleIds.Length != Ps5NativeParticleComputeRequest.ParticleIdByteCount ||
            particleCount > 6000)
        {
            throw new ArgumentException("small-particle draw history inputs are invalid");
        }

        for (var index = 0; index < particleCount; index++)
        {
            var particleId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                particleIds[(index * sizeof(uint))..]);
            if (particleId >= 6000)
            {
                throw new InvalidDataException($"particle ID {particleId} is out of range");
            }

            var recordOffset = checked((int)particleId * propertyStride);
            var priorLife = BitConverter.Int32BitsToSingle(unchecked((int)
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                    properties[(recordOffset + priorLifeOffset)..])));
            if (priorLife < 0.0f)
            {
                properties.Slice(recordOffset + currentLifeOffset, sizeof(float)).CopyTo(
                    properties.Slice(recordOffset + priorLifeOffset, sizeof(float)));
            }
        }
    }

    /// <summary>
    /// Runs the recovered PS5 particle compute shader in-process and returns its
    /// native 6000 x 0x44 property buffer. This first reusable boundary preserves
    /// the proven conformance runner verbatim while the Vulkan objects are being
    /// split into a persistent backend.
    /// </summary>
public static byte[] RunParticleCompute(Ps5NativeParticleComputeRequest request)
    {
        if (!request.IsValid)
        {
            throw new ArgumentException("invalid native particle compute request", nameof(request));
        }

        lock (ParticleComputeGate)
        {
            var tempRoot = Path.Combine(
                Path.GetTempPath(),
                "prosperismo-particle-compute-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            var spvPath = Path.Combine(
                tempRoot,
                request.SpawnWindow ? "particle.spawn.spv" : "particle.spv");
            var resourcesPath = Path.Combine(tempRoot, "resources.bin");
            var idsPath = Path.Combine(tempRoot, "ids.bin");
            var initialPropertiesPath = Path.Combine(tempRoot, "initial-properties.bin");
            var resourceSequenceRoot = Path.Combine(tempRoot, "resource-sequence");
            var resourceBanksRoot = Path.Combine(tempRoot, "resource-banks");
            var outputPath = Path.Combine(tempRoot, "properties.bin");
            File.WriteAllBytes(spvPath, request.ComputeSpirv.Span);
            File.WriteAllBytes(resourcesPath, request.Resources.Span);
            File.WriteAllBytes(idsPath, request.ParticleIds.Span);
            if (!request.InitialProperties.IsEmpty)
            {
                File.WriteAllBytes(initialPropertiesPath, request.InitialProperties.Span);
            }
            if (request.ResourceFrames is { Count: > 0 } resourceFrames)
            {
                Directory.CreateDirectory(resourceSequenceRoot);
                for (var index = 0; index < resourceFrames.Count; index++)
                {
                    File.WriteAllBytes(
                        Path.Combine(resourceSequenceRoot, $"frame-{index:D6}.bin"),
                        resourceFrames[index].Span);
                }
            }
            if (request.ResourceBankFrames is { Count: > 0 } resourceBanks)
            {
                Directory.CreateDirectory(resourceBanksRoot);
                for (var bankIndex = 0; bankIndex < resourceBanks.Count; bankIndex++)
                {
                    var bankRoot = Path.Combine(resourceBanksRoot, $"bank-{bankIndex:D2}");
                    Directory.CreateDirectory(bankRoot);
                    for (var frameIndex = 0; frameIndex < resourceBanks[bankIndex].Count; frameIndex++)
                    {
                        File.WriteAllBytes(
                            Path.Combine(bankRoot, $"frame-{frameIndex:D6}.bin"),
                            resourceBanks[bankIndex][frameIndex].Span);
                    }
                }
            }

            var names = new[]
            {
            "PROSPERISMO_PS5_PROBE_TIME",
            "PROSPERISMO_PS5_SIMULATION_START",
            "PROSPERISMO_PS5_PRE_SIMULATION",
            "PROSPERISMO_PS5_COMPUTE_RESOURCES",
            "PROSPERISMO_PS5_PARTICLE_IDS",
            "PROSPERISMO_PS5_INITIAL_PROPERTIES",
            "PROSPERISMO_PS5_ZERO_PROPERTIES",
            "PROSPERISMO_PS5_SPAWN_END",
            "PROSPERISMO_PS5_PROPERTY_OUTPUT",
            "PROSPERISMO_PS5_COMPUTE_RESOURCE_SEQUENCE",
            "PROSPERISMO_PS5_COMPUTE_RESOURCE_BANKS",
            "PROSPERISMO_PS5_INTERLEAVE_SMALL_DRAW_HISTORY",
            "PROSPERISMO_PS5_TRANS_PATTERN_FLAG",
        };
            var oldValues = names.ToDictionary(
                static name => name,
                static name => Environment.GetEnvironmentVariable(name),
                StringComparer.Ordinal);
            var oldExitCode = Environment.ExitCode;
            try
            {
                Environment.SetEnvironmentVariable(
                    "PROSPERISMO_PS5_PROBE_TIME",
                    request.SampleTime.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture));
                Environment.SetEnvironmentVariable(
                    "PROSPERISMO_PS5_SIMULATION_START",
                    request.SimulationStart.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture));
                Environment.SetEnvironmentVariable(
                    "PROSPERISMO_PS5_PRE_SIMULATION", request.PreSimulation ? "1" : null);
                Environment.SetEnvironmentVariable("PROSPERISMO_PS5_COMPUTE_RESOURCES", resourcesPath);
                Environment.SetEnvironmentVariable("PROSPERISMO_PS5_PARTICLE_IDS", idsPath);
                Environment.SetEnvironmentVariable(
                    "PROSPERISMO_PS5_INITIAL_PROPERTIES",
                    request.InitialProperties.IsEmpty ? null : initialPropertiesPath);
            Environment.SetEnvironmentVariable(
                "PROSPERISMO_PS5_ZERO_PROPERTIES", request.ZeroProperties ? "1" : null);
                Environment.SetEnvironmentVariable(
                    "PROSPERISMO_PS5_SPAWN_END",
                    request.SpawnEnd?.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture));
                Environment.SetEnvironmentVariable("PROSPERISMO_PS5_PROPERTY_OUTPUT", outputPath);
                Environment.SetEnvironmentVariable(
                    "PROSPERISMO_PS5_COMPUTE_RESOURCE_SEQUENCE",
                    request.ResourceFrames is { Count: > 0 } ? resourceSequenceRoot : null);
                Environment.SetEnvironmentVariable(
                    "PROSPERISMO_PS5_COMPUTE_RESOURCE_BANKS",
                    request.ResourceBankFrames is { Count: > 0 } ? resourceBanksRoot : null);
                Environment.SetEnvironmentVariable(
                    "PROSPERISMO_PS5_INTERLEAVE_SMALL_DRAW_HISTORY",
                    request.InterleaveSmallDrawHistory ? "1" : null);
                Environment.SetEnvironmentVariable(
                    "PROSPERISMO_PS5_TRANS_PATTERN_FLAG",
                    request.TransPatternFlag.ToString(
                        System.Globalization.CultureInfo.InvariantCulture));

                Run(["--ps5-particle", spvPath]);
                if (Environment.ExitCode != 0 || !File.Exists(outputPath))
                {
                    throw new InvalidOperationException(
                        $"native particle compute failed with exit code {Environment.ExitCode}");
                }
                return File.ReadAllBytes(outputPath);
            }
            finally
            {
                foreach (var name in names)
                {
                    Environment.SetEnvironmentVariable(name, oldValues[name]);
                }
                Environment.ExitCode = oldExitCode;
                try
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
                catch (IOException)
                {
                    // The OS can briefly retain a mapped shader file; temp cleanup
                    // is best effort and never changes the compute result.
                }
            }
        }
}

/// <summary>The byte-exact resource block used by the accepted second-bank probe.</summary>
public static byte[] CreateAcceptedBank1Resources() => Convert.FromHexString(
    "0000000000000000000000000000000000000000000000000000000000000000" +
    "0101000000000000280000007017000064000000010000000000004000000040" +
    "0000000000000000000020420000204200002042000020c20000f0c10000f041" +
    "00007a4400000000cdcccc3fcdcccc3fcdcccc3f00007a448fc2753d00000000" +
    "000000000000000001000000000000000000000000004842f7bc233ff7bc233f" +
    "4a51da3e000000000000c84200007ac500000000000000000000f041f304353f" +
    "f304353f00000000000000000000204200007ac5000000000000000000000000" +
    "000000000000000000000000000000000000000000000000");

/// <summary>The one-based ID order retained by the accepted second-bank baseline.</summary>
public static byte[] CreateAcceptedBank1Ids()
{
    var ids = new byte[Ps5NativeParticleComputeRequest.ParticleIdByteCount];
    for (var index = 0; index < 6000; index++)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            ids.AsSpan(index * sizeof(uint)),
            (uint)(index + 1));
    }
    return ids;
}

/// <summary>
/// Reproduces the two zero-based particle-ID permutations constructed by
/// NPXS40087 function <c>0x94020</c>. The second permutation continues from
/// the same renderer-global xorshift128+ state; the RNG is not reset between
/// buffers.
/// </summary>
public static (byte[] Primary, byte[] Secondary) CreateNativeParticleIdPermutations()
{
    const int count = 6000;
    ulong state0 = 0x112210F47DE98115UL;
    ulong state1 = 0x7BUL;

    byte[] BuildOne()
    {
        var values = new uint[count];
        for (uint index = 0; index < count; index++)
        {
            var oldState0 = state0;
            state0 = state1;
            var mixed = (oldState0 << 23) ^ oldState0;
            var next = (state0 >> 26) ^ state0 ^ mixed;
            state1 = (mixed >> 17) ^ next;
            var random32 = unchecked((uint)(state0 + state1));
            var slot = random32 % (index + 1);
            if (slot != index)
            {
                values[index] = values[slot];
            }
            values[slot] = index;
        }

        var bytes = new byte[count * sizeof(uint)];
        for (var index = 0; index < values.Length; index++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(index * sizeof(uint)),
                values[index]);
        }
        return bytes;
    }

    return (BuildOne(), BuildOne());
}

/// <summary>
/// The primary native ID buffer. The coldboot large-compute initializer at
/// <c>0x978e0</c> binds this same descriptor to both renderer groups; none of
/// coldboot's direct events uses opcode 11 to replace it with the secondary
/// descriptor.
/// </summary>
public static byte[] CreateNativePrimaryParticleIds() =>
    CreateNativeParticleIdPermutations().Primary;

public static void Run(string[] args)
    {
        const uint Sentinel = 0xCAFEBABE;

        var expectedFma = BitConverter.SingleToUInt32Bits(
            MathF.FusedMultiplyAdd(1.5f, 2.25f, 10.0f));
        var product = (long)0x7FFFFFFF * 0x00010003;
        var expectedHi = (uint)(product >> 32);
        var expectedLo = (uint)product;
        var expectedRestored = BitConverter.SingleToUInt32Bits(1.5f);

        // v_pk_fma_f16 of (0x4100, 0x7522, 0x04EA) per lane: the exact product
        // 2.5 * 21024 = 52560 is an f16 tie (between 0x7A6A and 0x7A6B), so the tiny
        // addend decides the rounding direction under a single fused rounding.
        const uint ExpectedPkFma = 0x7A6B_7A6B;
        const uint ExpectedPkFmaNeg = 0x7A6A_7A6A;
        // Both lanes of the pinned fma are ~52560, far above 1.0, so the clamp modifier
        // saturates each to 1.0 (0x3C00 in f16).
        const uint ExpectedPkFmaClamp = 0x3C00_3C00;

        unsafe
        {
            var particleMode = args.Length == 2 && args[0] == "--ps5-particle";
            var spvPath = args.Length > 0
                ? args[particleMode ? 1 : 0]
                : throw new InvalidOperationException(
                    "usage: Prosperismo.Tools.GpuConformance <path-to-exec-cs.spv> | " +
                    "--ps5-particle <path-to-particle_c.spv>");
            var code = File.ReadAllBytes(spvPath);
            var spawnWindow = particleMode &&
                Path.GetFileName(spvPath).Contains(".spawn.", StringComparison.OrdinalIgnoreCase);
            var probeTimeText = Environment.GetEnvironmentVariable("PROSPERISMO_PS5_PROBE_TIME");
            var probeTime = float.TryParse(
                probeTimeText,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsedProbeTime)
                ? Math.Max(parsedProbeTime, 0.0f)
                : 6.5f;
            var simulationStartText = Environment.GetEnvironmentVariable("PROSPERISMO_PS5_SIMULATION_START");
            var simulationStart = float.TryParse(
                simulationStartText,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsedSimulationStart)
                ? Math.Clamp(parsedSimulationStart, 0.0f, probeTime)
                : (spawnWindow ? 6.0f : probeTime);
            var forcePreSimulation = string.Equals(
                Environment.GetEnvironmentVariable("PROSPERISMO_PS5_PRE_SIMULATION"),
                "1",
                StringComparison.Ordinal);
            var preSimulation = particleMode &&
                Path.GetFileName(spvPath).Contains(".pre.", StringComparison.OrdinalIgnoreCase);
            var initialBuffers = particleMode
                ? CreatePs5ParticleBuffers(spawnWindow, preSimulation || forcePreSimulation, simulationStart)
                : new[] { CreateFilledBuffer(64, Sentinel) };
            var resourceSequencePath = Environment.GetEnvironmentVariable(
                "PROSPERISMO_PS5_COMPUTE_RESOURCE_SEQUENCE");
            var resourceSequence = particleMode &&
                !string.IsNullOrWhiteSpace(resourceSequencePath)
                    ? Directory.GetFiles(resourceSequencePath, "frame-*.bin")
                        .Order(StringComparer.Ordinal)
                        .Select(File.ReadAllBytes)
                        .ToArray()
                    : Array.Empty<byte[]>();
            if (resourceSequence.Any(static frame => frame.Length != Ps5NativeParticleComputeRequest.ResourceByteCount))
            {
                throw new InvalidDataException("particle resource sequence frames must be exactly 0xF8 bytes");
            }
            var resourceBanksPath = Environment.GetEnvironmentVariable(
                "PROSPERISMO_PS5_COMPUTE_RESOURCE_BANKS");
            var resourceBanks = particleMode && !string.IsNullOrWhiteSpace(resourceBanksPath)
                ? Directory.GetDirectories(resourceBanksPath, "bank-*")
                    .Order(StringComparer.Ordinal)
                    .Select(static bankRoot => Directory.GetFiles(bankRoot, "frame-*.bin")
                        .Order(StringComparer.Ordinal)
                        .Select(File.ReadAllBytes)
                        .ToArray())
                    .ToArray()
                : Array.Empty<byte[][]>();
            if (resourceBanks.Length != 0 &&
                (resourceBanks.Length != Ps5NativeParticleComputeRequest.SmallParticleBankCount ||
                 resourceBanks.SelectMany(static bank => bank).Any(static frame =>
                     frame.Length != Ps5NativeParticleComputeRequest.ResourceByteCount) ||
                 resourceBanks.Any(bank => bank.Length != resourceBanks[0].Length)))
            {
                throw new InvalidDataException(
                    "small-particle resource banks must contain eight equal 0xF8 frame sequences");
            }
            var bufferCount = initialBuffers.Length;

            var vk = Vk.GetApi();

            var appName = (byte*)SilkMarshal.StringToPtr("ProsperismoGpuConformance");
            var appInfo = new ApplicationInfo
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = appName,
                ApiVersion = Vk.Version13,
            };
            var instanceInfo = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &appInfo,
            };
            Check(vk.CreateInstance(in instanceInfo, null, out var instance), "vkCreateInstance");

            uint deviceCount = 0;
            vk.EnumeratePhysicalDevices(instance, &deviceCount, null);
            if (deviceCount == 0)
            {
                Console.WriteLine("no Vulkan devices found");
                return;
            }

            var physicalDevices = new PhysicalDevice[deviceCount];
            fixed (PhysicalDevice* pDevices = physicalDevices)
            {
                vk.EnumeratePhysicalDevices(instance, &deviceCount, pDevices);
            }

            // Prefer the first discrete GPU; fall back to the first device.
            var physical = physicalDevices[0];
            foreach (var candidate in physicalDevices)
            {
                vk.GetPhysicalDeviceProperties(candidate, out var props);
                Console.WriteLine(
                    $"Vulkan device: {SilkMarshal.PtrToString((nint)props.DeviceName)} ({props.DeviceType})");
                if (props.DeviceType == PhysicalDeviceType.DiscreteGpu)
                {
                    physical = candidate;
                    break;
                }
            }

            vk.GetPhysicalDeviceProperties(physical, out var chosenProps);
            Console.WriteLine(
                $"executing on: {SilkMarshal.PtrToString((nint)chosenProps.DeviceName)}");

            uint familyCount = 0;
            vk.GetPhysicalDeviceQueueFamilyProperties(physical, &familyCount, null);
            var families = new QueueFamilyProperties[familyCount];
            fixed (QueueFamilyProperties* pFamilies = families)
            {
                vk.GetPhysicalDeviceQueueFamilyProperties(physical, &familyCount, pFamilies);
            }

            uint? computeFamilyFound = null;
            for (uint index = 0; index < familyCount; index++)
            {
                if (families[index].QueueFlags.HasFlag(QueueFlags.ComputeBit))
                {
                    computeFamilyFound = index;
                    break;
                }
            }

            var computeFamily = computeFamilyFound
                ?? throw new InvalidOperationException("device has no compute-capable queue family");

            // The emitted SPIR-V declares the Int64 capability.
            vk.GetPhysicalDeviceFeatures(physical, out var supportedFeatures);
            if (!supportedFeatures.ShaderInt64)
            {
                throw new InvalidOperationException(
                    "device does not support shaderInt64, which the emitted SPIR-V requires");
            }

            var priority = 1f;
            var queueInfo = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = computeFamily,
                QueueCount = 1,
                PQueuePriorities = &priority,
            };
            var subgroupFeatures = new PhysicalDeviceSubgroupSizeControlFeatures
            {
                SType = StructureType.PhysicalDeviceSubgroupSizeControlFeatures,
            };
            var subgroupProperties = new PhysicalDeviceSubgroupSizeControlProperties
            {
                SType = StructureType.PhysicalDeviceSubgroupSizeControlProperties,
            };
            var properties2 = new PhysicalDeviceProperties2
            {
                SType = StructureType.PhysicalDeviceProperties2,
                PNext = &subgroupProperties,
            };
            var features2 = new PhysicalDeviceFeatures2
            {
                SType = StructureType.PhysicalDeviceFeatures2,
                PNext = &subgroupFeatures,
            };
            vk.GetPhysicalDeviceProperties2(physical, &properties2);
            vk.GetPhysicalDeviceFeatures2(physical, &features2);
            var canRequireWave32 = subgroupFeatures.SubgroupSizeControl &&
                subgroupProperties.MinSubgroupSize <= 32 &&
                subgroupProperties.MaxSubgroupSize >= 32 &&
                subgroupProperties.RequiredSubgroupSizeStages.HasFlag(ShaderStageFlags.ComputeBit);
            if (particleMode && !canRequireWave32)
            {
                throw new InvalidOperationException(
                    "the PS5 particle probe requires a 32-lane compute subgroup, but this Vulkan device cannot require wave32");
            }

            var enabledSubgroupFeatures = new PhysicalDeviceSubgroupSizeControlFeatures
            {
                SType = StructureType.PhysicalDeviceSubgroupSizeControlFeatures,
                SubgroupSizeControl = canRequireWave32,
                ComputeFullSubgroups = canRequireWave32 && subgroupFeatures.ComputeFullSubgroups,
            };
            var features = new PhysicalDeviceFeatures { ShaderInt64 = true };
            var deviceInfo = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                PNext = particleMode ? &enabledSubgroupFeatures : null,
                QueueCreateInfoCount = 1,
                PQueueCreateInfos = &queueInfo,
                PEnabledFeatures = &features,
            };
            Check(vk.CreateDevice(physical, in deviceInfo, null, out var device), "vkCreateDevice");
            vk.GetDeviceQueue(device, computeFamily, 0, out var queue);

            vk.GetPhysicalDeviceMemoryProperties(physical, out var memoryProperties);
            var buffers = new Silk.NET.Vulkan.Buffer[bufferCount];
            var memories = new DeviceMemory[bufferCount];
            var mappedPointers = new nint[bufferCount];
            for (var bufferIndex = 0; bufferIndex < bufferCount; bufferIndex++)
            {
                var bufferInfo = new BufferCreateInfo
                {
                    SType = StructureType.BufferCreateInfo,
                    Size = (ulong)initialBuffers[bufferIndex].Length,
                    Usage = BufferUsageFlags.StorageBufferBit,
                    SharingMode = SharingMode.Exclusive,
                };
                Check(
                    vk.CreateBuffer(device, in bufferInfo, null, out buffers[bufferIndex]),
                    $"vkCreateBuffer[{bufferIndex}]");
                vk.GetBufferMemoryRequirements(device, buffers[bufferIndex], out var requirements);

                var memoryType = FindHostMemoryType(memoryProperties, requirements.MemoryTypeBits);
                var allocateInfo = new MemoryAllocateInfo
                {
                    SType = StructureType.MemoryAllocateInfo,
                    AllocationSize = requirements.Size,
                    MemoryTypeIndex = memoryType,
                };
                Check(
                    vk.AllocateMemory(device, in allocateInfo, null, out memories[bufferIndex]),
                    $"vkAllocateMemory[{bufferIndex}]");
                Check(
                    vk.BindBufferMemory(device, buffers[bufferIndex], memories[bufferIndex], 0),
                    $"vkBindBufferMemory[{bufferIndex}]");

                void* mapped;
                Check(
                    vk.MapMemory(
                        device,
                        memories[bufferIndex],
                        0,
                        (ulong)initialBuffers[bufferIndex].Length,
                        0,
                        &mapped),
                    $"vkMapMemory[{bufferIndex}]");
                initialBuffers[bufferIndex].CopyTo(
                    new Span<byte>(mapped, initialBuffers[bufferIndex].Length));
                mappedPointers[bufferIndex] = (nint)mapped;
            }

            // Prosperismo emits all guest buffers as one descriptor array at set 0,
            // binding 0. The PS5 particle probe resolves four entries in evaluator
            // order: SRTCs, ResourcesCs, particleProperties, particleIds1.
            ShaderModule module;
            fixed (byte* pCode = code)
            {
                var moduleInfo = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)code.Length,
                    PCode = (uint*)pCode,
                };
                Check(vk.CreateShaderModule(device, in moduleInfo, null, out module), "vkCreateShaderModule");
            }

            var layoutBinding = new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = (uint)bufferCount,
                StageFlags = ShaderStageFlags.ComputeBit,
            };
            var setLayoutInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 1,
                PBindings = &layoutBinding,
            };
            Check(
                vk.CreateDescriptorSetLayout(device, in setLayoutInfo, null, out var setLayout),
                "vkCreateDescriptorSetLayout");

            var pushConstantRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.ComputeBit,
                Offset = 0,
                Size = 3 * sizeof(uint),
            };
            var pipelineLayoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = &setLayout,
                PushConstantRangeCount = particleMode ? 1u : 0u,
                PPushConstantRanges = particleMode ? &pushConstantRange : null,
            };
            Check(
                vk.CreatePipelineLayout(device, in pipelineLayoutInfo, null, out var pipelineLayout),
                "vkCreatePipelineLayout");

            var entryName = (byte*)SilkMarshal.StringToPtr("main");
            var requiredSubgroupSize = new PipelineShaderStageRequiredSubgroupSizeCreateInfo
            {
                SType = StructureType.PipelineShaderStageRequiredSubgroupSizeCreateInfo,
                RequiredSubgroupSize = 32,
            };
            var pipelineInfo = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    PNext = particleMode ? &requiredSubgroupSize : null,
                    Flags = particleMode && enabledSubgroupFeatures.ComputeFullSubgroups
                        ? PipelineShaderStageCreateFlags.RequireFullSubgroupsBit
                        : 0,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = module,
                    PName = entryName,
                },
                Layout = pipelineLayout,
            };
            Check(
                vk.CreateComputePipelines(device, default, 1, in pipelineInfo, null, out var pipeline),
                "vkCreateComputePipelines");
            Console.WriteLine("driver accepted the SPIR-V (pipeline created)");
            if (particleMode)
            {
                Console.WriteLine("compute subgroup pinned to 32 lanes");
            }

            var poolSize = new DescriptorPoolSize
            {
                Type = DescriptorType.StorageBuffer,
                DescriptorCount = (uint)bufferCount,
            };
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = 1,
                PoolSizeCount = 1,
                PPoolSizes = &poolSize,
            };
            Check(vk.CreateDescriptorPool(device, in poolInfo, null, out var pool), "vkCreateDescriptorPool");

            var setAllocateInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = pool,
                DescriptorSetCount = 1,
                PSetLayouts = &setLayout,
            };
            Check(vk.AllocateDescriptorSets(device, in setAllocateInfo, out var descriptorSet), "vkAllocateDescriptorSets");

            var descriptorBuffers = new DescriptorBufferInfo[bufferCount];
            for (var index = 0; index < bufferCount; index++)
            {
                descriptorBuffers[index] = new DescriptorBufferInfo
                {
                    Buffer = buffers[index],
                    Offset = 0,
                    Range = (ulong)initialBuffers[index].Length,
                };
            }

            fixed (DescriptorBufferInfo* pDescriptorBuffers = descriptorBuffers)
            {
                var write = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = descriptorSet,
                    DstBinding = 0,
                    DstArrayElement = 0,
                    DescriptorCount = (uint)bufferCount,
                    DescriptorType = DescriptorType.StorageBuffer,
                    PBufferInfo = pDescriptorBuffers,
                };
                vk.UpdateDescriptorSets(device, 1, in write, 0, null);
            }

            var commandPoolInfo = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = computeFamily,
                Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            };
            Check(vk.CreateCommandPool(device, in commandPoolInfo, null, out var commandPool), "vkCreateCommandPool");

            var commandBufferInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            Check(vk.AllocateCommandBuffers(device, in commandBufferInfo, out var commandBuffer), "vkAllocateCommandBuffers");

            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
            };
            Check(vk.BeginCommandBuffer(commandBuffer, in beginInfo), "vkBeginCommandBuffer");
            vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, pipeline);
            vk.CmdBindDescriptorSets(
                commandBuffer,
                PipelineBindPoint.Compute,
                pipelineLayout,
                0,
                1,
                in descriptorSet,
                0,
                null);
            var particleDispatchGroups = 1u;
            if (particleMode)
            {
                var dispatchParticles = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                    initialBuffers[1].AsSpan(0x28));
                particleDispatchGroups = ParticleDispatchGroupCount(dispatchParticles);
                uint* threadLimits = stackalloc uint[3] { dispatchParticles, 1, 1 };
                vk.CmdPushConstants(
                    commandBuffer,
                    pipelineLayout,
                    ShaderStageFlags.ComputeBit,
                    0,
                    3 * sizeof(uint),
                    threadLimits);
            }
            vk.CmdDispatch(commandBuffer, particleDispatchGroups, 1, 1);
            var barrier = new MemoryBarrier
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.ShaderWriteBit,
                DstAccessMask = AccessFlags.HostReadBit,
            };
            vk.CmdPipelineBarrier(
                commandBuffer,
                PipelineStageFlags.ComputeShaderBit,
                PipelineStageFlags.HostBit,
                0,
                1,
                in barrier,
                0,
                null,
                0,
                null);
            Check(vk.EndCommandBuffer(commandBuffer), "vkEndCommandBuffer");

            var submitInfo = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
            };
            Check(vk.QueueSubmit(queue, 1, in submitInfo, default), "vkQueueSubmit");
            Check(vk.QueueWaitIdle(queue), "vkQueueWaitIdle");

            if (particleMode)
            {
                // The native pre-simulation pass writes the off-screen 100000 sentinel.
                // Clear only isPreSimulation and run the ordinary step against the
                // can turn that initialized state into visible particle properties.
                var mappedSrt = new Span<byte>((void*)mappedPointers[0], initialBuffers[0].Length);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(mappedSrt[0x14..], 0);
                // Advance from the 6.0-second spawn window to the requested native
                // sample. Field 10 clears the spawn bit at 6.1 s.
                var followupSteps = Math.Max(
                    particleMode ? 0 : 1,
                    (int)MathF.Round((probeTime - simulationStart) * 60.0f));
                var mappedResources = new Span<byte>((void*)mappedPointers[1], initialBuffers[1].Length);
                var dispatchParticles = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                    mappedResources[0x28..]);
                if (resourceBanks.Length != 0)
                {
                    if (resourceBanks[0].Length != followupSteps + 1)
                    {
                        throw new InvalidDataException(
                            "small-particle bank frame count does not match the requested simulation interval");
                    }
                    for (var bankIndex = 1; bankIndex < resourceBanks.Length; bankIndex++)
                    {
                        var initialResource = resourceBanks[bankIndex][0].AsSpan();
                        if (!IsActiveSmallParticleResource(initialResource))
                        {
                            continue;
                        }
                        initialResource[0x20..].CopyTo(mappedResources[0x20..]);
                        dispatchParticles = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                            mappedResources[0x28..]);
                        DispatchParticleResources(
                            vk, queue, commandBuffer, pipeline, pipelineLayout, descriptorSet,
                            beginInfo, barrier, dispatchParticles, $"bank-{bankIndex}-frame-0");
                    }
                    var propertySequenceOutput = Environment.GetEnvironmentVariable(
                        "PROSPERISMO_PS5_PROPERTY_SEQUENCE_OUTPUT");
                    if (!string.IsNullOrWhiteSpace(propertySequenceOutput))
                    {
                        Directory.CreateDirectory(propertySequenceOutput);
                        File.WriteAllBytes(
                            Path.Combine(propertySequenceOutput, "frame-000000.bin"),
                            new ReadOnlySpan<byte>(
                                (void*)mappedPointers[2], initialBuffers[2].Length).ToArray());
                    }
                }
                var spawnEndText = Environment.GetEnvironmentVariable("PROSPERISMO_PS5_SPAWN_END");
                var hasSpawnEnd = float.TryParse(
                    spawnEndText,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var spawnEnd);
                if (!hasSpawnEnd && spawnWindow)
                {
                    spawnEnd = 6.1f;
                    hasSpawnEnd = true;
                }
                for (var step = 0; step < followupSteps; step++)
                {
                    if (particleMode)
                    {
                        if (Environment.GetEnvironmentVariable(
                                "PROSPERISMO_PS5_INTERLEAVE_SMALL_DRAW_HISTORY") == "1")
                        {
                            var mappedProperties = new Span<byte>(
                                (void*)mappedPointers[2], initialBuffers[2].Length);
                            var mappedIds = new ReadOnlySpan<byte>(
                                (void*)mappedPointers[3], initialBuffers[3].Length);
                            if (resourceBanks.Length != 0)
                            {
                                for (var bankIndex = 0; bankIndex < resourceBanks.Length; bankIndex++)
                                {
                                    var previousResource = resourceBanks[bankIndex][step].AsSpan();
                                    if (IsActiveSmallParticleResource(previousResource))
                                    {
                                        var previousCount = System.Buffers.Binary.BinaryPrimitives
                                            .ReadUInt32LittleEndian(previousResource[0x28..]);
                                        ApplySmallDrawHistory(mappedProperties, mappedIds, previousCount);
                                    }
                                }
                            }
                            else
                            {
                                ApplySmallDrawHistory(mappedProperties, mappedIds, dispatchParticles);
                            }
                        }
                        var nativeTime = simulationStart + ((step + 1) / 60.0f);
                        WriteSingle(mappedSrt[0x08..], nativeTime);
                        if (resourceBanks.Length != 0)
                        {
                            for (var bankIndex = 0; bankIndex < resourceBanks.Length; bankIndex++)
                            {
                                var currentResource = resourceBanks[bankIndex][step + 1].AsSpan();
                                if (!IsActiveSmallParticleResource(currentResource))
                                {
                                    continue;
                                }
                                currentResource[0x20..].CopyTo(
                                    mappedResources[0x20..]);
                                dispatchParticles = System.Buffers.Binary.BinaryPrimitives
                                    .ReadUInt32LittleEndian(mappedResources[0x28..]);
                                DispatchParticleResources(
                                    vk, queue, commandBuffer, pipeline, pipelineLayout, descriptorSet,
                                    beginInfo, barrier, dispatchParticles,
                                    $"bank-{bankIndex}-frame-{step + 1}");
                            }
                            var propertySequenceOutput = Environment.GetEnvironmentVariable(
                                "PROSPERISMO_PS5_PROPERTY_SEQUENCE_OUTPUT");
                            if (!string.IsNullOrWhiteSpace(propertySequenceOutput))
                            {
                                Directory.CreateDirectory(propertySequenceOutput);
                                File.WriteAllBytes(
                                    Path.Combine(
                                        propertySequenceOutput,
                                        $"frame-{step + 1:D6}.bin"),
                                    new ReadOnlySpan<byte>(
                                        (void*)mappedPointers[2], initialBuffers[2].Length).ToArray());
                            }
                            continue;
                        }
                        if (resourceSequence.Length > step + 1)
                        {
                            // The first 0x20 bytes are live host descriptors.
                            // and must be refreshed for this exact native frame.
                            resourceSequence[step + 1].AsSpan(0x20).CopyTo(
                                mappedResources[0x20..]);
                        }
                        else if (hasSpawnEnd && nativeTime >= spawnEnd)
                        {
                            var particleOptions = System.Buffers.Binary.BinaryPrimitives
                                .ReadUInt32LittleEndian(mappedResources[0x20..]);
                            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                                mappedResources[0x20..],
                                particleOptions & ~0x1000u);
                        }
                    }

                    dispatchParticles = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                        mappedResources[0x28..]);
                    DispatchParticleResources(
                        vk, queue, commandBuffer, pipeline, pipelineLayout, descriptorSet,
                        beginInfo, barrier, dispatchParticles, $"frame-{step + 1}");
                }
            }

            var failures = 0;
            if (particleMode)
            {
                const int propertyStride = 0x44;
                var propertyBytes = new ReadOnlySpan<byte>(
                    (void*)mappedPointers[2],
                    initialBuffers[2].Length);
                var changedBytes = 0;
                for (var index = 0; index < propertyBytes.Length; index++)
                {
                    changedBytes += propertyBytes[index] == initialBuffers[2][index] ? 0 : 1;
                }

                var hash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(propertyBytes));
                var readbackPath = Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(spvPath))!,
                    Path.GetFileNameWithoutExtension(spvPath) + ".properties.bin");
                readbackPath = Environment.GetEnvironmentVariable("PROSPERISMO_PS5_PROPERTY_OUTPUT")
                    ?? readbackPath;
                File.WriteAllBytes(readbackPath, propertyBytes.ToArray());
                Console.WriteLine(
                    $"particleProperties: changed_bytes={changedBytes}/{propertyBytes.Length} sha256={hash}");
                Console.WriteLine($"particleProperties: readback={readbackPath}");

                var populatedRecords = 0;
                for (var particleIndex = 0; particleIndex < 6000; particleIndex++)
                {
                    var record = propertyBytes.Slice(particleIndex * propertyStride, propertyStride);
                    if (record.SequenceEqual(
                            initialBuffers[2].AsSpan(
                                particleIndex * propertyStride,
                                propertyStride)))
                    {
                        continue;
                    }

                    populatedRecords++;
                    if (populatedRecords <= 6)
                    {
                        Console.WriteLine(
                            $"particle[{particleIndex}] " +
                            $"pos=({ReadSingle(record, 0):G7},{ReadSingle(record, 4):G7},{ReadSingle(record, 8):G7}) " +
                            $"vel=({ReadSingle(record, 0x10):G7},{ReadSingle(record, 0x14):G7},{ReadSingle(record, 0x18):G7}) " +
                            $"life={ReadSingle(record, 0x38):G7}/{ReadSingle(record, 0x3C):G7} " +
                            $"ren={ReadSingle(record, 0x40):G7}");
                    }
                }

                Console.WriteLine($"particleProperties: populated_records={populatedRecords}/6000");
                if (changedBytes == 0 || populatedRecords == 0)
                {
                    failures++;
                    Console.WriteLine("FAIL  Sony particle_c completed without observable property writes");
                }
                else
                {
                    Console.WriteLine("PASS  Sony particle_c wrote the native 0x44-byte property buffer");
                }
            }
            else
            {
                var words = (uint*)mappedPointers[0];
                var results = new (string Name, uint Actual, uint Expected)[]
                {
            ("v_fmac_f32  fma(1.5, 2.25, 10.0)", words[0], expectedFma),
            ("v_mul_hi_i32 hi(0x7FFFFFFF*0x10003)", words[1], expectedHi),
            ("v_mul_lo_i32 lo(0x7FFFFFFF*0x10003)", words[2], expectedLo),
            ("exec=0 store suppressed (offset 12 sentinel)", words[3], Sentinel),
            ("store after exec restore (offset 16)", words[4], expectedRestored),
            ("v_pk_fma_f16 fused rounds up at midpoint", words[5], ExpectedPkFma),
            ("v_pk_fma_f16 neg addend rounds down", words[6], ExpectedPkFmaNeg),
            ("v_pk_fma_f16 clamp saturates to 1.0", words[7], ExpectedPkFmaClamp),
                };
                foreach (var (name, actual, expected) in results)
                {
                    var status = actual == expected ? "PASS" : "FAIL";
                    if (actual != expected)
                    {
                        failures++;
                    }

                    Console.WriteLine($"{status}  {name}: gpu=0x{actual:X8} expected=0x{expected:X8}");
                }

                var totalWords = initialBuffers[0].Length / sizeof(uint);
                var trailingClobbered = 0;
                for (var index = results.Length; index < totalWords; index++)
                {
                    if (words[index] != Sentinel)
                    {
                        trailingClobbered++;
                        Console.WriteLine(
                            $"FAIL  trailing word [{index}] clobbered: gpu=0x{words[index]:X8} expected=0x{Sentinel:X8}");
                    }
                }

                failures += trailingClobbered;
                if (trailingClobbered == 0)
                {
                    Console.WriteLine(
                        $"PASS  trailing words [{results.Length}..{totalWords - 1}] intact (sentinel)");
                }
            }

            Console.WriteLine(failures == 0
                ? "RESULT: all values match"
                : $"RESULT: {failures} mismatch(es)");

            vk.DestroyCommandPool(device, commandPool, null);
            vk.DestroyDescriptorPool(device, pool, null);
            vk.DestroyPipeline(device, pipeline, null);
            vk.DestroyPipelineLayout(device, pipelineLayout, null);
            vk.DestroyDescriptorSetLayout(device, setLayout, null);
            vk.DestroyShaderModule(device, module, null);
            for (var index = 0; index < bufferCount; index++)
            {
                vk.UnmapMemory(device, memories[index]);
                vk.FreeMemory(device, memories[index], null);
                vk.DestroyBuffer(device, buffers[index], null);
            }
            vk.DestroyDevice(device, null);
            vk.DestroyInstance(instance, null);

            Environment.ExitCode = failures == 0 ? 0 : 1;

            static byte[] CreateFilledBuffer(int byteCount, uint value)
            {
                var result = new byte[byteCount];
                for (var offset = 0; offset < byteCount; offset += sizeof(uint))
                {
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                        result.AsSpan(offset),
                        value);
                }

                return result;
            }

            static byte[][] CreatePs5ParticleBuffers(
                bool spawnWindow,
                bool preSimulation,
                float simulationStart)
            {
                const ulong resourcesAddress = 0x0110_0000UL;
                const ulong propertiesAddress = 0x0200_0000UL;
                const ulong idsAddress = 0x0300_0000UL;
                const int propertyStride = 0x44;
                const int maxParticleId = 6000;

        var resources = CreateAcceptedBank1Resources();
                var resourcesOverride = Environment.GetEnvironmentVariable(
                    "PROSPERISMO_PS5_COMPUTE_RESOURCES");
                if (!string.IsNullOrWhiteSpace(resourcesOverride))
                {
                    resources = File.ReadAllBytes(resourcesOverride);
                    if (resources.Length != 0xF8)
                    {
                        throw new InvalidDataException(
                            "large compute resource override must be exactly 0xF8 bytes");
                    }
                }
                if (spawnWindow)
                {
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                        resources.AsSpan(0x20),
                        0x1101);
                }
                WriteRawBufferDescriptor(resources.AsSpan(0x00, 0x10), idsAddress, 4, maxParticleId);
                WriteRawBufferDescriptor(
                    resources.AsSpan(0x10, 0x10),
                    propertiesAddress,
                    propertyStride,
                    maxParticleId);

                var srt = new byte[0x1C];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(srt, resourcesAddress);
                WriteSingle(srt.AsSpan(0x08), simulationStart);
                WriteSingle(srt.AsSpan(0x0C), 1.0f / 60.0f);
                WriteSingle(srt.AsSpan(0x10), 1.0f);
                // A standalone zeroed property buffer needs the shader's native
                // pre-simulation path to initialise particles before ordinary steps.
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                    srt.AsSpan(0x14),
                    preSimulation ? 1u : 0u);
                var transPatternFlagText = Environment.GetEnvironmentVariable(
                    "PROSPERISMO_PS5_TRANS_PATTERN_FLAG");
                var transPatternFlag = uint.TryParse(
                    transPatternFlagText,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsedTransPatternFlag)
                    ? parsedTransPatternFlag
                    : 0u;
                if (transPatternFlag > byte.MaxValue)
                {
                    throw new InvalidDataException(
                        "particle transition-pattern flag must fit its two native nibbles");
                }
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                    srt.AsSpan(0x18),
                    transPatternFlag);

                var properties = new byte[propertyStride * maxParticleId];
                var ids = new byte[sizeof(uint) * maxParticleId];
                var zeroProperties = string.Equals(
                    Environment.GetEnvironmentVariable("PROSPERISMO_PS5_ZERO_PROPERTIES"),
                    "1",
                    StringComparison.Ordinal);
                var idsOverride = Environment.GetEnvironmentVariable("PROSPERISMO_PS5_PARTICLE_IDS");
                if (!string.IsNullOrWhiteSpace(idsOverride))
                {
                    ids = File.ReadAllBytes(idsOverride);
                    if (ids.Length != sizeof(uint) * maxParticleId)
                    {
                        throw new InvalidDataException(
                            "particle ID override must be exactly 24,000 bytes");
                    }
                }
                else
                {
                    // Legacy proof input retained for the accepted bank-1 baseline.
                    // Exact host initialization is supplied through the override above.
            ids = CreateAcceptedBank1Ids();
                }

                var initialPropertiesOverride = Environment.GetEnvironmentVariable(
                    "PROSPERISMO_PS5_INITIAL_PROPERTIES");
                if (!string.IsNullOrWhiteSpace(initialPropertiesOverride))
                {
                    properties = File.ReadAllBytes(initialPropertiesOverride);
                    if (properties.Length != propertyStride * maxParticleId)
                    {
                        throw new InvalidDataException(
                            "initial particle properties must be exactly 408,000 bytes");
                    }
                }

                if (string.IsNullOrWhiteSpace(initialPropertiesOverride) && !zeroProperties)
                {
                    // Legacy proof input retained for the accepted bank-1 baseline.
                    for (var index = 0; index < maxParticleId; index++)
                    {
                        WriteSingle(properties.AsSpan(index * propertyStride + 0x38), -1.0f);
                        WriteSingle(properties.AsSpan(index * propertyStride + 0x40), -1.0f);
                    }
                }

                return [srt, resources, properties, ids];
            }

            static void WriteRawBufferDescriptor(
                Span<byte> destination,
                ulong baseAddress,
                int stride,
                int recordCount)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                    destination,
                    (uint)baseAddress);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                    destination[4..],
                    (uint)(baseAddress >> 32) | ((uint)stride << 16));
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                    destination[8..],
                    (uint)recordCount);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], 0);
            }

            static void WriteSingle(Span<byte> destination, float value) =>
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                    destination,
                    BitConverter.SingleToUInt32Bits(value));

            static uint FindHostMemoryType(
                PhysicalDeviceMemoryProperties properties,
                uint supportedBits)
            {
                for (var index = 0; index < properties.MemoryTypeCount; index++)
                {
                    var flags = properties.MemoryTypes[index].PropertyFlags;
                    if ((supportedBits & (1u << index)) != 0 &&
                        flags.HasFlag(MemoryPropertyFlags.HostVisibleBit) &&
                        flags.HasFlag(MemoryPropertyFlags.HostCoherentBit))
                    {
                        return (uint)index;
                    }
                }

                throw new InvalidOperationException(
                    "no host-visible, host-coherent memory type available for a probe buffer");
            }

            static void Check(Result result, string what)
            {
                if (result != Result.Success)
                {
                    throw new InvalidOperationException($"{what} failed: {result}");
                }
            }

            static float ReadSingle(ReadOnlySpan<byte> bytes, int offset) =>
                BitConverter.Int32BitsToSingle(
                    unchecked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..])));
        }

    }

    private static unsafe void DispatchParticleResources(
        Vk vk,
        Queue queue,
        CommandBuffer commandBuffer,
        Pipeline pipeline,
        PipelineLayout pipelineLayout,
        DescriptorSet descriptorSet,
        CommandBufferBeginInfo beginInfo,
        MemoryBarrier barrier,
        uint particleCount,
        string operation)
    {
        static void Require(Result result, string call)
        {
            if (result != Result.Success)
            {
                throw new InvalidOperationException($"{call} failed: {result}");
            }
        }

        uint* limits = stackalloc uint[3] { particleCount, 1, 1 };
        Require(vk.ResetCommandBuffer(commandBuffer, 0), $"vkResetCommandBuffer({operation})");
        Require(vk.BeginCommandBuffer(commandBuffer, in beginInfo), $"vkBeginCommandBuffer({operation})");
        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, pipeline);
        vk.CmdBindDescriptorSets(
            commandBuffer,
            PipelineBindPoint.Compute,
            pipelineLayout,
            0,
            1,
            in descriptorSet,
            0,
            null);
        vk.CmdPushConstants(
            commandBuffer,
            pipelineLayout,
            ShaderStageFlags.ComputeBit,
            0,
            3 * sizeof(uint),
            limits);
        vk.CmdDispatch(commandBuffer, ParticleDispatchGroupCount(particleCount), 1, 1);
        vk.CmdPipelineBarrier(
            commandBuffer,
            PipelineStageFlags.ComputeShaderBit,
            PipelineStageFlags.HostBit,
            0,
            1,
            in barrier,
            0,
            null,
            0,
            null);
        Require(vk.EndCommandBuffer(commandBuffer), $"vkEndCommandBuffer({operation})");
        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
        };
        Require(vk.QueueSubmit(queue, 1, in submitInfo, default), $"vkQueueSubmit({operation})");
        Require(vk.QueueWaitIdle(queue), $"vkQueueWaitIdle({operation})");
    }
}
