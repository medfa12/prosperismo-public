// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using System.Diagnostics;

namespace Prosperismo.Libs.Presentation;

/// <summary>
/// The four guest storage buffers bound by NPXS40087's translated
/// <c>particle_c</c> shader for one small-particle bank.
/// </summary>
public sealed record Ps5NativeParticleComputeBank(
    ReadOnlyMemory<byte> SrtCs,
    ReadOnlyMemory<byte> ResourcesCs,
    ReadOnlyMemory<byte> ParticleIds)
{
    public bool IsValid =>
        SrtCs.Length == Ps5NativeParticleComputeBackend.SrtByteCount &&
        ResourcesCs.Length == Ps5NativeParticleComputeRequest.ResourceByteCount &&
        ParticleIds.Length == Ps5NativeParticleComputeRequest.ParticleIdByteCount;
}

/// <summary>
/// Per-frame state for one bank. The backend keeps the ID and property
/// </summary>
public sealed record Ps5NativeParticleComputeBankFrame(
    ReadOnlyMemory<byte> SrtCs,
    ReadOnlyMemory<byte> ResourcesCs)
{
    public bool IsValid =>
        SrtCs.Length == Ps5NativeParticleComputeBackend.SrtByteCount &&
        ResourcesCs.Length == Ps5NativeParticleComputeRequest.ResourceByteCount;
}

/// <summary>One ordered NPXS40087 small-bank simulation step.</summary>
public sealed record Ps5NativeParticleComputeFrame(
    IReadOnlyList<Ps5NativeParticleComputeBankFrame> Banks)
{
    public bool IsValid =>
        Banks.Count == Ps5NativeParticleComputeRequest.SmallParticleBankCount &&
        Banks.All(static bank => bank.IsValid);

    public bool IsValidFor(int bankCount) =>
        bankCount > 0 && Banks.Count == bankCount && Banks.All(static bank => bank.IsValid);
}

/// <summary>
/// One draw block whose vertex-stage life latch must be folded into the shared
/// particle-property allocation before the next compute step.
/// </summary>
public sealed record Ps5NativeParticleDrawHistory(
    ReadOnlyMemory<byte> ResourcesVsPs,
    bool IsLarge);

/// <summary>
/// Persistent Vulkan implementation of the recovered NPXS40087
/// <c>particle_c</c> ABI.
///
/// <para>The descriptor shape is deliberately the one used by
/// <see cref="GpuConformanceRunner"/>: set 0, binding 0, four storage buffers
/// in the order SRTCs, ResourcesCs, particleProperties, particleIds1. Eight
/// descriptor sets select the eight resource/SRT/ID banks, while every set's
/// third descriptor points at the same 6000 × 0x44 property allocation.</para>
///
/// <para>Pipeline, descriptor layout/pool/sets, command buffer, mapped guest
/// buffers, and the property allocation are created once. Dispatch updates
/// the mapped SRT/resource blocks, records the active banks in order, and
/// exposes the resulting shared property bytes after the queue completes.</para>
/// </summary>
public sealed unsafe class Ps5NativeParticleComputeBackend : IDisposable
{
    public const int SrtByteCount = 0x1C;

    private const uint RequiredSubgroupSize = 32;
    private const uint DescriptorCount = 4;

    private readonly Vk _vk = Ps5VulkanApi.Create();
    private readonly MappedStorageBuffer[] _srtBuffers;
    private readonly MappedStorageBuffer[] _resourceBuffers;
    private readonly MappedStorageBuffer[] _idBuffers;
    private readonly MappedStorageBuffer _propertyBuffer;
    private readonly DescriptorSet[] _descriptorSets;
    private readonly byte[] _propertySnapshot =
        new byte[Ps5NativeParticleComputeRequest.ParticlePropertyByteCount];

    private Instance _instance;
    private PhysicalDevice _physicalDevice;
    private Device _device;
    private Queue _queue;
    private CommandPool _commandPool;
    private CommandBuffer _commandBuffer;
    private DescriptorSetLayout _setLayout;
    private DescriptorPool _descriptorPool;
    private PipelineLayout _pipelineLayout;
    private ShaderModule _shaderModule;
    private Pipeline _pipeline;
    private PhysicalDeviceMemoryProperties _memoryProperties;
    private bool _disposed;

    public Ps5NativeParticleComputeBackend(
        ReadOnlyMemory<byte> computeSpirv,
        IReadOnlyList<Ps5NativeParticleComputeBank> banks,
        ReadOnlyMemory<byte> initialProperties)
    {
        ValidateConstructorInputs(computeSpirv, banks, initialProperties);
        _srtBuffers = new MappedStorageBuffer[banks.Count];
        _resourceBuffers = new MappedStorageBuffer[banks.Count];
        _idBuffers = new MappedStorageBuffer[banks.Count];
        _descriptorSets = new DescriptorSet[banks.Count];
        _propertyBuffer = null!;

        try
        {
            var initializationClock = Stopwatch.StartNew();
            CreateDeviceAndPipeline(computeSpirv.Span);
            TraceInitialization(
                $"native compute create: device-pipeline={initializationClock.Elapsed.TotalMilliseconds:0.0}ms");
            initializationClock.Restart();
            _propertyBuffer = CreateMappedBuffer(initialProperties.Span);
            TraceInitialization(
                $"native compute create: properties={initializationClock.Elapsed.TotalMilliseconds:0.0}ms");
            initializationClock.Restart();
            initialProperties.Span.CopyTo(_propertySnapshot);
            for (var bankIndex = 0; bankIndex < banks.Count; bankIndex++)
            {
                _srtBuffers[bankIndex] = CreateMappedBuffer(banks[bankIndex].SrtCs.Span);
                _resourceBuffers[bankIndex] = CreateMappedBuffer(
                    Ps5NativeParticleProgramCompiler.CreateSmallParticleComputeResources(
                        banks[bankIndex].ResourcesCs.Span));
                _idBuffers[bankIndex] = CreateMappedBuffer(banks[bankIndex].ParticleIds.Span);
            }
            TraceInitialization(
                $"native compute create: bank-buffers={initializationClock.Elapsed.TotalMilliseconds:0.0}ms");
            initializationClock.Restart();

            CreateDescriptors();
            TraceInitialization(
                $"native compute create: descriptors={initializationClock.Elapsed.TotalMilliseconds:0.0}ms");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private static void TraceInitialization(string message)
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("PROSPERISMO_PS5_NATIVE_TRACE"),
                "1",
                StringComparison.Ordinal))
        {
            Console.Error.WriteLine(message);
        }
    }

    /// <summary>The Vulkan device selected for the persistent session.</summary>
    public string DeviceName { get; private set; } = "unknown";

    /// <summary>Number of ordered particle banks owned by this session.</summary>
    public int BankCount => _descriptorSets.Length;

    /// <summary>
    /// Returns the exact workgroup count used by the translated shader. The
    /// shader's local size is 64 and the push constant remains the exclusive
    /// particle bound, matching the conformance path.
    /// </summary>
    public static uint DispatchGroupCount(uint particleCount) =>
        GpuConformanceRunner.ParticleDispatchGroupCount(particleCount);

    /// <summary>
    /// Dispatches one frame of all active banks against the shared property
    /// allocation. Inactive resource records are skipped as the native
    /// eight-system walk skips a null/zero-max-particle system.
    /// </summary>
    public void Dispatch(Ps5NativeParticleComputeFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frame is null || !frame.IsValidFor(BankCount))
        {
            throw new ArgumentException("invalid NPXS40087 particle frame", nameof(frame));
        }

        // The previous call is synchronous. Keeping this wait here also makes
        // the ownership rule safe if the implementation later becomes queued.
        Check(_vk.QueueWaitIdle(_queue), "vkQueueWaitIdle(before particle frame)");
        for (var bankIndex = 0; bankIndex < BankCount; bankIndex++)
        {
            _srtBuffers[bankIndex].Write(frame.Banks[bankIndex].SrtCs.Span);
            _resourceBuffers[bankIndex].Write(
                Ps5NativeParticleProgramCompiler.CreateSmallParticleComputeResources(
                    frame.Banks[bankIndex].ResourcesCs.Span));
        }

        Span<int> activeBankIndices = stackalloc int[BankCount];
        Span<uint> activeParticleCounts = stackalloc uint[BankCount];
        var activeBankCount = 0;
        for (var bankIndex = 0; bankIndex < BankCount; bankIndex++)
        {
            var resource = frame.Banks[bankIndex].ResourcesCs.Span;
            if (!GpuConformanceRunner.IsActiveSmallParticleResource(resource))
            {
                continue;
            }

            var particleCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                resource[0x28..]);
            activeBankIndices[activeBankCount] = bankIndex;
            activeParticleCounts[activeBankCount] = particleCount;
            activeBankCount++;
        }

        Check(_vk.ResetCommandBuffer(_commandBuffer, 0), "vkResetCommandBuffer(particle frame)");
        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
        };
        Check(_vk.BeginCommandBuffer(_commandBuffer, in begin), "vkBeginCommandBuffer(particle frame)");

        if (activeBankCount > 0)
        {
            var hostToCompute = new MemoryBarrier
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.HostWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.HostBit,
                PipelineStageFlags.ComputeShaderBit,
                0,
                1,
                in hostToCompute,
                0,
                null,
                0,
                null);

            _vk.CmdBindPipeline(_commandBuffer, PipelineBindPoint.Compute, _pipeline);
            uint* limits = stackalloc uint[3];
            for (var activeIndex = 0; activeIndex < activeBankCount; activeIndex++)
            {
                var bankIndex = activeBankIndices[activeIndex];
                var particleCount = activeParticleCounts[activeIndex];
                var descriptorSet = _descriptorSets[bankIndex];
                _vk.CmdBindDescriptorSets(
                    _commandBuffer,
                    PipelineBindPoint.Compute,
                    _pipelineLayout,
                    0,
                    1,
                    in descriptorSet,
                    0,
                    null);
                limits[0] = particleCount;
                limits[1] = 1;
                limits[2] = 1;
                _vk.CmdPushConstants(
                    _commandBuffer,
                    _pipelineLayout,
                    ShaderStageFlags.ComputeBit,
                    0,
                    3 * sizeof(uint),
                    limits);
                _vk.CmdDispatch(_commandBuffer, DispatchGroupCount(particleCount), 1, 1);

                var betweenBanks = new MemoryBarrier
                {
                    SType = StructureType.MemoryBarrier,
                    SrcAccessMask = AccessFlags.ShaderWriteBit,
                    DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    PipelineStageFlags.ComputeShaderBit,
                    PipelineStageFlags.ComputeShaderBit,
                    0,
                    1,
                    in betweenBanks,
                    0,
                    null,
                    0,
                    null);
            }

            var computeToHost = new MemoryBarrier
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.ShaderWriteBit,
                DstAccessMask = AccessFlags.HostReadBit,
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.ComputeShaderBit,
                PipelineStageFlags.HostBit,
                0,
                1,
                in computeToHost,
                0,
                null,
                0,
                null);
        }

        Check(_vk.EndCommandBuffer(_commandBuffer), "vkEndCommandBuffer(particle frame)");
        var commandBuffer = _commandBuffer;
        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
        };
        Check(_vk.QueueSubmit(_queue, 1, in submit, default), "vkQueueSubmit(particle frame)");
        Check(_vk.QueueWaitIdle(_queue), "vkQueueWaitIdle(particle frame)");

        _propertyBuffer.CopyTo(_propertySnapshot);
    }

    /// <summary>
    /// Copies the shared 6000 × 0x44 property allocation without exposing a
    /// mapped pointer or transferring ownership of the Vulkan buffer.
    /// </summary>
    public void CopyPropertiesTo(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (destination.Length != _propertySnapshot.Length)
        {
            throw new ArgumentException(
                $"property destination must be exactly {_propertySnapshot.Length} bytes",
                nameof(destination));
        }

        _propertySnapshot.AsSpan().CopyTo(destination);
    }

    /// <summary>Returns a copy of the latest completed shared property state.</summary>
    public byte[] CopyProperties()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return (byte[])_propertySnapshot.Clone();
    }

    /// <summary>
    /// Applies the verified <c>particle_vv</c> draw-history latch to the
    /// persistent shared property allocation before the next compute step.
    /// The draw ABI indexes records with its own invocation limit, base,
    /// stride, and capacity fields; these are deliberately not the compute
    /// stage's particle count and offset fields.
    /// </summary>
    public void ApplySmallDrawHistory(IReadOnlyList<ReadOnlyMemory<byte>> previousDrawResources)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(previousDrawResources);
        if (previousDrawResources.Count != BankCount)
        {
            throw new ArgumentException("invalid selector-1 draw-history inputs");
        }

        for (var bankIndex = 0; bankIndex < previousDrawResources.Count; bankIndex++)
        {
            var resource = previousDrawResources[bankIndex].Span;
            if (resource.IsEmpty)
            {
                continue;
            }

            ApplyDrawHistoryRange(_propertySnapshot, resource, isLarge: false);
        }

        _propertyBuffer.Write(_propertySnapshot);
    }

    /// <summary>
    /// The input is not constrained to <see cref="BankCount"/> because one
    /// compute allocation contains small and large logical banks while the two
    /// draw families are submitted through separate graphics pipelines.
    /// </summary>
    public void ApplyDrawHistory(IReadOnlyList<Ps5NativeParticleDrawHistory> previousDraws)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(previousDraws);
        foreach (var draw in previousDraws)
        {
            if (draw.ResourcesVsPs.IsEmpty)
            {
                continue;
            }

            ApplyDrawHistoryRange(
                _propertySnapshot,
                draw.ResourcesVsPs.Span,
                draw.IsLarge);
        }

        _propertyBuffer.Write(_propertySnapshot);
    }

    /// <summary>
    /// Replays the vertex shader's <c>renLife</c> latch for one authored draw
    /// range. Exposed so byte-level conformance tests can pin both resource
    /// layouts without constructing a Vulkan device.
    /// </summary>
    public static void ApplyDrawHistoryRange(
        Span<byte> properties,
        ReadOnlySpan<byte> drawResource,
        bool isLarge)
    {
        const int propertyStride = 0x44;
        const int currentLifeOffset = 0x38;
        const int renderLifeOffset = 0x40;
        var invocationOffset = isLarge ? 0xAC : 0x20;
        var requiredLength = invocationOffset + 0x10;
        if (drawResource.Length < requiredLength)
        {
            throw new ArgumentException("particle draw resource is truncated");
        }

        var invocationLimit = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            drawResource[invocationOffset..]);
        var baseRecordIndex = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            drawResource[(invocationOffset + 4)..]);
        var recordIndexStride = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            drawResource[(invocationOffset + 8)..]);
        var recordCapacity = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            drawResource[(invocationOffset + 12)..]);
        if (recordIndexStride == 0 || recordCapacity == 0)
        {
            return;
        }

        for (uint invocation = 0; invocation < invocationLimit; invocation++)
        {
            var recordIndex = checked(baseRecordIndex + (invocation * recordIndexStride));
            if (recordIndex >= recordCapacity || recordIndex >= 6000)
            {
                continue;
            }

            var recordOffset = checked((int)recordIndex * propertyStride);
            var renderLife = BitConverter.Int32BitsToSingle(unchecked((int)
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                    properties[(recordOffset + renderLifeOffset)..])));
            if (renderLife < 0.0f)
            {
                properties.Slice(recordOffset + currentLifeOffset, sizeof(float)).CopyTo(
                    properties.Slice(recordOffset + renderLifeOffset, sizeof(float)));
            }
        }
    }

    private static void ValidateConstructorInputs(
        ReadOnlyMemory<byte> computeSpirv,
        IReadOnlyList<Ps5NativeParticleComputeBank> banks,
        ReadOnlyMemory<byte> initialProperties)
    {
        ArgumentNullException.ThrowIfNull(banks);
        if (computeSpirv.IsEmpty || computeSpirv.Length % sizeof(uint) != 0)
        {
            throw new ArgumentException("particle_c SPIR-V must be a non-empty uint32 stream", nameof(computeSpirv));
        }

        // NPXS40087 walks eight small and two large systems for each of two
        // transition instances. All twenty logical banks share one property
        // allocation during a hand-off.
        if (banks.Count is < 1 or > 20 || banks.Any(static bank => !bank.IsValid))
        {
            throw new ArgumentException("NPXS40087 requires one to twenty valid particle banks", nameof(banks));
        }

        if (initialProperties.Length != Ps5NativeParticleComputeRequest.ParticlePropertyByteCount)
        {
            throw new ArgumentException(
                $"initial properties must be exactly {Ps5NativeParticleComputeRequest.ParticlePropertyByteCount} bytes",
                nameof(initialProperties));
        }
    }

    private void CreateDeviceAndPipeline(ReadOnlySpan<byte> computeSpirv)
    {
        var appName = (byte*)SilkMarshal.StringToPtr("ProsperismoPs5ParticleCompute");
        nint portability = 0;
        try
        {
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
            byte** instanceExtensions = stackalloc byte*[1];
            if (Ps5VulkanApi.RequiresPortabilityEnumeration(AppContext.BaseDirectory))
            {
                portability = SilkMarshal.StringToPtr("VK_KHR_portability_enumeration");
                instanceExtensions[0] = (byte*)portability;
                instanceInfo.EnabledExtensionCount = 1;
                instanceInfo.PpEnabledExtensionNames = instanceExtensions;
                instanceInfo.Flags = InstanceCreateFlags.EnumeratePortabilityBitKhr;
            }

            Check(_vk.CreateInstance(in instanceInfo, null, out _instance), "vkCreateInstance(particle compute)");
        }
        finally
        {
            SilkMarshal.Free((nint)appName);
            if (portability != 0)
            {
                SilkMarshal.Free(portability);
            }
        }

        uint deviceCount = 0;
        Check(_vk.EnumeratePhysicalDevices(_instance, &deviceCount, null), "vkEnumeratePhysicalDevices(count)");
        if (deviceCount == 0)
        {
            throw new InvalidOperationException("no Vulkan device supports NPXS40087 particle compute");
        }

        var physicalDevices = new PhysicalDevice[deviceCount];
        fixed (PhysicalDevice* devices = physicalDevices)
        {
            Check(_vk.EnumeratePhysicalDevices(_instance, &deviceCount, devices), "vkEnumeratePhysicalDevices");
        }

        _physicalDevice = physicalDevices[0];
        foreach (var candidate in physicalDevices)
        {
            var properties = _vk.GetPhysicalDeviceProperties(candidate);
            if (properties.DeviceType == PhysicalDeviceType.DiscreteGpu)
            {
                _physicalDevice = candidate;
                break;
            }
        }

        var selectedProperties = _vk.GetPhysicalDeviceProperties(_physicalDevice);
        DeviceName = SilkMarshal.PtrToString((nint)selectedProperties.DeviceName) ?? "unknown";
        _vk.GetPhysicalDeviceFeatures(_physicalDevice, out var supportedFeatures);
        if (!supportedFeatures.ShaderInt64)
        {
            throw new InvalidOperationException("NPXS40087 particle_c requires shaderInt64");
        }

        uint familyCount = 0;
        _vk.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, &familyCount, null);
        var families = new QueueFamilyProperties[familyCount];
        fixed (QueueFamilyProperties* familyData = families)
        {
            _vk.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, &familyCount, familyData);
        }

        uint computeFamily = uint.MaxValue;
        for (var index = 0u; index < familyCount; index++)
        {
            if (families[index].QueueFlags.HasFlag(QueueFlags.ComputeBit))
            {
                computeFamily = index;
                break;
            }
        }

        if (computeFamily == uint.MaxValue)
        {
            throw new InvalidOperationException("Vulkan device has no compute queue family");
        }

        var supportedSubgroupFeatures = new PhysicalDeviceSubgroupSizeControlFeatures
        {
            SType = StructureType.PhysicalDeviceSubgroupSizeControlFeatures,
        };
        var subgroup = new PhysicalDeviceSubgroupProperties
        {
            SType = StructureType.PhysicalDeviceSubgroupProperties,
        };
        var subgroupProperties = new PhysicalDeviceSubgroupSizeControlProperties
        {
            SType = StructureType.PhysicalDeviceSubgroupSizeControlProperties,
        };
        var properties2 = new PhysicalDeviceProperties2
        {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = &subgroup,
        };
        var features2 = new PhysicalDeviceFeatures2
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &supportedSubgroupFeatures,
        };
        _vk.GetPhysicalDeviceProperties2(_physicalDevice, &properties2);
        properties2.PNext = &subgroupProperties;
        _vk.GetPhysicalDeviceProperties2(_physicalDevice, &properties2);
        _vk.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
        var canRequireWave32 = supportedSubgroupFeatures.SubgroupSizeControl &&
            subgroupProperties.MinSubgroupSize <= RequiredSubgroupSize &&
            subgroupProperties.MaxSubgroupSize >= RequiredSubgroupSize &&
            subgroupProperties.RequiredSubgroupSizeStages.HasFlag(ShaderStageFlags.ComputeBit);
        var fixedWave32 = subgroup.SubgroupSize == RequiredSubgroupSize &&
            subgroup.SupportedStages.HasFlag(ShaderStageFlags.ComputeBit);
        if (!canRequireWave32 && !fixedWave32)
        {
            throw new InvalidOperationException(
                "NPXS40087 particle_c requires a Vulkan compute subgroup size of 32");
        }

        var priority = 1f;
        var queueInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = computeFamily,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };
        var enabledSubgroupFeatures = new PhysicalDeviceSubgroupSizeControlFeatures
        {
            SType = StructureType.PhysicalDeviceSubgroupSizeControlFeatures,
            SubgroupSizeControl = canRequireWave32,
            ComputeFullSubgroups = canRequireWave32 &&
                supportedSubgroupFeatures.ComputeFullSubgroups,
        };
        var enabledFeatures = new PhysicalDeviceFeatures { ShaderInt64 = true };
        nint portabilityDevice = 0;
        try
        {
            byte** deviceExtensions = stackalloc byte*[1];
            var deviceInfo = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                PNext = canRequireWave32 ? &enabledSubgroupFeatures : null,
                QueueCreateInfoCount = 1,
                PQueueCreateInfos = &queueInfo,
                PEnabledFeatures = &enabledFeatures,
            };
            if (OperatingSystem.IsMacOS())
            {
                portabilityDevice = SilkMarshal.StringToPtr("VK_KHR_portability_subset");
                deviceExtensions[0] = (byte*)portabilityDevice;
                deviceInfo.EnabledExtensionCount = 1;
                deviceInfo.PpEnabledExtensionNames = deviceExtensions;
            }

            Check(_vk.CreateDevice(_physicalDevice, in deviceInfo, null, out _device), "vkCreateDevice(particle compute)");
        }
        finally
        {
            if (portabilityDevice != 0)
            {
                SilkMarshal.Free(portabilityDevice);
            }
        }

        _vk.GetDeviceQueue(_device, computeFamily, 0, out _queue);
        _memoryProperties = _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice);
        var commandPoolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = computeFamily,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };
        Check(_vk.CreateCommandPool(_device, in commandPoolInfo, null, out _commandPool), "vkCreateCommandPool(particle compute)");
        var commandAllocate = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        Check(_vk.AllocateCommandBuffers(_device, in commandAllocate, out _commandBuffer), "vkAllocateCommandBuffers(particle compute)");

        var moduleInfo = new ShaderModuleCreateInfo
        {
            SType = StructureType.ShaderModuleCreateInfo,
            CodeSize = (nuint)computeSpirv.Length,
        };
        fixed (byte* code = computeSpirv)
        {
            moduleInfo.PCode = (uint*)code;
            Check(_vk.CreateShaderModule(_device, in moduleInfo, null, out _shaderModule), "vkCreateShaderModule(particle_c)");
        }

        var pushRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Offset = 0,
            Size = 3 * sizeof(uint),
        };
        var layoutBinding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.StorageBuffer,
            DescriptorCount = DescriptorCount,
            StageFlags = ShaderStageFlags.ComputeBit,
        };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &layoutBinding,
        };
        Check(_vk.CreateDescriptorSetLayout(_device, in layoutInfo, null, out _setLayout), "vkCreateDescriptorSetLayout(particle_c)");
        var setLayouts = stackalloc DescriptorSetLayout[1] { _setLayout };
        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = setLayouts,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange,
        };
        Check(_vk.CreatePipelineLayout(_device, in pipelineLayoutInfo, null, out _pipelineLayout), "vkCreatePipelineLayout(particle_c)");

        var entry = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            var requiredSubgroupSize = new PipelineShaderStageRequiredSubgroupSizeCreateInfo
            {
                SType = StructureType.PipelineShaderStageRequiredSubgroupSizeCreateInfo,
                RequiredSubgroupSize = RequiredSubgroupSize,
            };
            var pipelineInfo = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    PNext = canRequireWave32 ? &requiredSubgroupSize : null,
                    Flags = canRequireWave32 && enabledSubgroupFeatures.ComputeFullSubgroups
                        ? PipelineShaderStageCreateFlags.RequireFullSubgroupsBit
                        : 0,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = _shaderModule,
                    PName = entry,
                },
                Layout = _pipelineLayout,
            };
            using var pipelineCache = Ps5VulkanPipelineCache.Create(
                _vk,
                _device,
                $"particle-compute-wave32-required={canRequireWave32}-full={enabledSubgroupFeatures.ComputeFullSubgroups}",
                computeSpirv.ToArray());
            Check(
                _vk.CreateComputePipelines(
                    _device,
                    pipelineCache.Handle,
                    1,
                    in pipelineInfo,
                    null,
                    out _pipeline),
                "vkCreateComputePipelines(particle_c)");
            pipelineCache.Persist();
        }
        finally
        {
            SilkMarshal.Free((nint)entry);
        }
    }

    private MappedStorageBuffer CreateMappedBuffer(ReadOnlySpan<byte> initialData)
    {
        Ps5ParticleDrawProbe.CreateBuffer(
            _vk,
            _device,
            _memoryProperties,
            (ulong)initialData.Length,
            BufferUsageFlags.StorageBufferBit,
            hostVisible: true,
            out var buffer,
            out var memory);
        try
        {
            return new MappedStorageBuffer(_vk, _device, buffer, memory, initialData);
        }
        catch
        {
            _vk.DestroyBuffer(_device, buffer, null);
            _vk.FreeMemory(_device, memory, null);
            throw;
        }
    }

    private void CreateDescriptors()
    {
        var poolSize = new DescriptorPoolSize
        {
            Type = DescriptorType.StorageBuffer,
            DescriptorCount = DescriptorCount * (uint)BankCount,
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = (uint)BankCount,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
        };
        Check(_vk.CreateDescriptorPool(_device, in poolInfo, null, out _descriptorPool), "vkCreateDescriptorPool(particle_c)");
        var setLayouts = stackalloc DescriptorSetLayout[1] { _setLayout };
        var descriptors = stackalloc DescriptorBufferInfo[(int)DescriptorCount];

        for (var bankIndex = 0; bankIndex < BankCount; bankIndex++)
        {
            var setAllocateInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = setLayouts,
            };
            Check(_vk.AllocateDescriptorSets(_device, in setAllocateInfo, out _descriptorSets[bankIndex]), "vkAllocateDescriptorSets(particle_c)");

            descriptors[0] = new DescriptorBufferInfo { Buffer = _srtBuffers[bankIndex].Buffer, Range = _srtBuffers[bankIndex].Size };
            descriptors[1] = new DescriptorBufferInfo { Buffer = _resourceBuffers[bankIndex].Buffer, Range = _resourceBuffers[bankIndex].Size };
            descriptors[2] = new DescriptorBufferInfo { Buffer = _propertyBuffer.Buffer, Range = _propertyBuffer.Size };
            descriptors[3] = new DescriptorBufferInfo { Buffer = _idBuffers[bankIndex].Buffer, Range = _idBuffers[bankIndex].Size };
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _descriptorSets[bankIndex],
                DstBinding = 0,
                DescriptorCount = DescriptorCount,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = descriptors,
            };
            _vk.UpdateDescriptorSets(_device, 1, in write, 0, null);
        }
    }

    private static void Check(Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw new InvalidOperationException($"{operation} failed: {result}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_device.Handle != 0)
        {
            _vk.QueueWaitIdle(_queue);
            foreach (var buffer in _srtBuffers) buffer?.Dispose();
            foreach (var buffer in _resourceBuffers) buffer?.Dispose();
            foreach (var buffer in _idBuffers) buffer?.Dispose();
            _propertyBuffer?.Dispose();
            if (_descriptorPool.Handle != 0) _vk.DestroyDescriptorPool(_device, _descriptorPool, null);
            if (_pipeline.Handle != 0) _vk.DestroyPipeline(_device, _pipeline, null);
            if (_pipelineLayout.Handle != 0) _vk.DestroyPipelineLayout(_device, _pipelineLayout, null);
            if (_setLayout.Handle != 0) _vk.DestroyDescriptorSetLayout(_device, _setLayout, null);
            if (_shaderModule.Handle != 0) _vk.DestroyShaderModule(_device, _shaderModule, null);
            if (_commandPool.Handle != 0) _vk.DestroyCommandPool(_device, _commandPool, null);
            _vk.DestroyDevice(_device, null);
        }

        if (_instance.Handle != 0)
        {
            _vk.DestroyInstance(_instance, null);
        }

        _vk.Dispose();
    }

    private sealed unsafe class MappedStorageBuffer : IDisposable
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private void* _mapped;
        private bool _disposed;

        public MappedStorageBuffer(
            Vk vk,
            Device device,
            Silk.NET.Vulkan.Buffer buffer,
            DeviceMemory memory,
            ReadOnlySpan<byte> initialData)
        {
            _vk = vk;
            _device = device;
            Buffer = buffer;
            Memory = memory;
            Size = (ulong)initialData.Length;
            void* mapped;
            Check(_vk.MapMemory(_device, Memory, 0, Size, 0, &mapped), "vkMapMemory(particle_c)");
            _mapped = mapped;
            initialData.CopyTo(new Span<byte>(_mapped, initialData.Length));
        }

        public Silk.NET.Vulkan.Buffer Buffer { get; }
        public DeviceMemory Memory { get; }
        public ulong Size { get; }

        public void Write(ReadOnlySpan<byte> data)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if ((ulong)data.Length != Size)
            {
                throw new ArgumentException("mapped particle buffer update has the wrong size", nameof(data));
            }

            data.CopyTo(new Span<byte>(_mapped, data.Length));
        }

        public void CopyTo(Span<byte> destination)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if ((ulong)destination.Length != Size)
            {
                throw new ArgumentException("mapped particle buffer copy has the wrong size", nameof(destination));
            }

            new ReadOnlySpan<byte>(_mapped, destination.Length).CopyTo(destination);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _vk.UnmapMemory(_device, Memory);
            _vk.DestroyBuffer(_device, Buffer, null);
            _vk.FreeMemory(_device, Memory, null);
        }
    }
}
