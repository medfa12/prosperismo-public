// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Prosperismo.ShaderCompiler.Vulkan;

namespace Prosperismo.Shell.BackgroundPoc;

/// <summary>
/// Dispatches a translated PS5 compute shader on this host's GPU.
///
/// <para>The existing <c>Ps5ParticleVulkanSession</c> is graphics-only, so
/// there was no way to run <c>particle_c</c> — the shader that actually moves
/// the background's particles. This is the minimum compute path: storage
/// buffers at set 0 / binding 0 as an array, matching the convention the
/// graphics session already uses, then a dispatch and a readback.</para>
/// </summary>
internal sealed unsafe class ParticleComputeRunner : IDisposable
{
    private readonly Vk _vk = Vk.GetApi();
    private Instance _instance;
    private Device _device;
    private Queue _queue;
    private uint _queueFamily;
    private CommandPool _commandPool;
    private readonly List<(Silk.NET.Vulkan.Buffer Buffer, DeviceMemory Memory, int Size)> _buffers = [];

    internal string DeviceName { get; private set; } = "unknown";

    internal ParticleComputeRunner()
    {
        var appName = (byte*)SilkMarshal.StringToPtr("ParticleComputeRunner");
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

        // MoltenVK is a portability implementation; without this flag the
        // loader reports no conformant driver even though it is installed.
        nint portability = 0;
        byte** extensions = stackalloc byte*[1];
        if (OperatingSystem.IsMacOS())
        {
            portability = SilkMarshal.StringToPtr("VK_KHR_portability_enumeration");
            extensions[0] = (byte*)portability;
            instanceInfo.EnabledExtensionCount = 1;
            instanceInfo.PpEnabledExtensionNames = extensions;
            instanceInfo.Flags = InstanceCreateFlags.EnumeratePortabilityBitKhr;
        }

        var created = _vk.CreateInstance(in instanceInfo, null, out _instance);
        SilkMarshal.Free((nint)appName);
        if (portability != 0)
        {
            SilkMarshal.Free(portability);
        }

        Check(created, "vkCreateInstance");

        uint count = 0;
        Check(_vk.EnumeratePhysicalDevices(_instance, &count, null), "enumerate devices");
        var physicals = new PhysicalDevice[count];
        fixed (PhysicalDevice* p = physicals)
        {
            Check(_vk.EnumeratePhysicalDevices(_instance, &count, p), "enumerate devices");
        }

        var physical = physicals[0];
        var properties = _vk.GetPhysicalDeviceProperties(physical);
        DeviceName = SilkMarshal.PtrToString((nint)properties.DeviceName) ?? "unknown";
        Physical = physical;

        uint families = 0;
        _vk.GetPhysicalDeviceQueueFamilyProperties(physical, &families, null);
        var familyProperties = new QueueFamilyProperties[families];
        fixed (QueueFamilyProperties* p = familyProperties)
        {
            _vk.GetPhysicalDeviceQueueFamilyProperties(physical, &families, p);
        }

        _queueFamily = uint.MaxValue;
        for (var i = 0u; i < families; i++)
        {
            if ((familyProperties[i].QueueFlags & QueueFlags.ComputeBit) != 0)
            {
                _queueFamily = i;
                break;
            }
        }

        if (_queueFamily == uint.MaxValue)
        {
            throw new InvalidOperationException("no compute queue family");
        }

        var priority = 1f;
        var queueInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = _queueFamily,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };

        // MoltenVK requires VK_KHR_portability_subset on the device too.
        var subset = SilkMarshal.StringToPtr("VK_KHR_portability_subset");
        byte** deviceExtensions = stackalloc byte*[1];
        deviceExtensions[0] = (byte*)subset;
        var deviceInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &queueInfo,
            EnabledExtensionCount = OperatingSystem.IsMacOS() ? 1u : 0u,
            PpEnabledExtensionNames = OperatingSystem.IsMacOS() ? deviceExtensions : null,
        };
        var deviceResult = _vk.CreateDevice(physical, in deviceInfo, null, out _device);
        SilkMarshal.Free(subset);
        Check(deviceResult, "vkCreateDevice");

        _vk.GetDeviceQueue(_device, _queueFamily, 0, out _queue);

        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = _queueFamily,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };
        Check(_vk.CreateCommandPool(_device, in poolInfo, null, out _commandPool), "vkCreateCommandPool");
    }

    private PhysicalDevice Physical { get; }

    /// <summary>
    /// Uploads <paramref name="contents"/> as storage buffers, dispatches
    /// <paramref name="spirv"/> over <paramref name="groups"/> workgroups, and
    /// returns the buffers as the shader left them.
    /// </summary>
    internal byte[][] Dispatch(byte[] spirv, byte[][] contents, uint groups, uint threadLimit = 0)
    {
        // Every index below is relative to the start of _buffers, so a second
        // call would otherwise download the first call's buffers.
        ReleaseBuffers();

        foreach (var data in contents)
        {
            CreateBuffer(data);
        }

        var layoutBinding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.StorageBuffer,
            DescriptorCount = (uint)contents.Length,
            StageFlags = ShaderStageFlags.ComputeBit,
        };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &layoutBinding,
        };
        Check(_vk.CreateDescriptorSetLayout(_device, in layoutInfo, null, out var setLayout),
            "vkCreateDescriptorSetLayout");

        var poolSize = new DescriptorPoolSize
        {
            Type = DescriptorType.StorageBuffer,
            DescriptorCount = (uint)contents.Length,
        };
        var descriptorPoolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
        };
        Check(_vk.CreateDescriptorPool(_device, in descriptorPoolInfo, null, out var descriptorPool),
            "vkCreateDescriptorPool");

        var allocateInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout,
        };
        Check(_vk.AllocateDescriptorSets(_device, in allocateInfo, out var descriptorSet),
            "vkAllocateDescriptorSets");

        var bufferInfos = stackalloc DescriptorBufferInfo[contents.Length];
        for (var i = 0; i < contents.Length; i++)
        {
            bufferInfos[i] = new DescriptorBufferInfo
            {
                Buffer = _buffers[i].Buffer,
                Offset = 0,
                Range = (ulong)_buffers[i].Size,
            };
        }

        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = 0,
            DescriptorCount = (uint)contents.Length,
            DescriptorType = DescriptorType.StorageBuffer,
            PBufferInfo = bufferInfos,
        };
        _vk.UpdateDescriptorSets(_device, 1, in write, 0, null);

        fixed (byte* code = spirv)
        {
            var moduleInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length,
                PCode = (uint*)code,
            };
            Check(_vk.CreateShaderModule(_device, in moduleInfo, null, out var module),
                "vkCreateShaderModule");

            // The translator injects a per-axis thread limit as a push constant:
            // Vulkan dispatches whole workgroups, so the command path has to
            // supply the exact exclusive bound. Leaving the range undeclared
            // leaves it zero and masks off every thread in the dispatch.
            var pushRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.ComputeBit,
                Offset = 0,
                Size = 12,
            };
            var pipelineLayoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = &setLayout,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushRange,
            };
            Check(_vk.CreatePipelineLayout(_device, in pipelineLayoutInfo, null, out var pipelineLayout),
                "vkCreatePipelineLayout");

            var entry = (byte*)SilkMarshal.StringToPtr("main");
            var pipelineInfo = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = module,
                    PName = entry,
                },
                Layout = pipelineLayout,
            };
            var pipelineResult = _vk.CreateComputePipelines(
                _device, default, 1, in pipelineInfo, null, out var pipeline);
            SilkMarshal.Free((nint)entry);
            Check(pipelineResult, "vkCreateComputePipelines");

            var commandInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            Check(_vk.AllocateCommandBuffers(_device, in commandInfo, out var commandBuffer),
                "vkAllocateCommandBuffers");

            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            Check(_vk.BeginCommandBuffer(commandBuffer, in beginInfo), "vkBeginCommandBuffer");
            _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, pipeline);
            _vk.CmdBindDescriptorSets(
                commandBuffer, PipelineBindPoint.Compute, pipelineLayout, 0, 1, in descriptorSet, 0, null);
            var limits = stackalloc uint[3];
            limits[0] = threadLimit == 0 ? uint.MaxValue : threadLimit;
            limits[1] = 1;
            limits[2] = 1;
            _vk.CmdPushConstants(
                commandBuffer, pipelineLayout, ShaderStageFlags.ComputeBit, 0, 12, limits);
            _vk.CmdDispatch(commandBuffer, groups, 1, 1);
            Check(_vk.EndCommandBuffer(commandBuffer), "vkEndCommandBuffer");

            var submit = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
            };
            Check(_vk.QueueSubmit(_queue, 1, in submit, default), "vkQueueSubmit");
            Check(_vk.QueueWaitIdle(_queue), "vkQueueWaitIdle");
        }

        var results = new byte[contents.Length][];
        for (var i = 0; i < contents.Length; i++)
        {
            results[i] = Download(i);
        }

        return results;
    }


    /// <summary>
    /// Renders a translated PS5 <em>pixel</em> shader over a fullscreen
    /// triangle and returns the framebuffer as RGBA.
    ///
    /// <para><c>fw_background_p</c> is the background's base plate and reads
    /// fragment stage is the whole picture. The existing ripple renderer could
    /// not carry it: that path hardcodes the ripple ABI of a 40-byte constant
    /// buffer and two textures.</para>
    /// </summary>
    internal byte[] RenderFragment(
        byte[] vertexSpirv,
        byte[] fragmentSpirv,
        byte[][] contents,
        uint width,
        uint height)
    {
        foreach (var data in contents)
        {
            CreateBuffer(data);
        }

        var layoutBinding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.StorageBuffer,
            DescriptorCount = (uint)Math.Max(1, contents.Length),
            StageFlags = ShaderStageFlags.FragmentBit,
        };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &layoutBinding,
        };
        Check(_vk.CreateDescriptorSetLayout(_device, in layoutInfo, null, out var setLayout),
            "vkCreateDescriptorSetLayout");

        var poolSize = new DescriptorPoolSize
        {
            Type = DescriptorType.StorageBuffer,
            DescriptorCount = (uint)Math.Max(1, contents.Length),
        };
        var descriptorPoolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
        };
        Check(_vk.CreateDescriptorPool(_device, in descriptorPoolInfo, null, out var descriptorPool),
            "vkCreateDescriptorPool");

        var allocateInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout,
        };
        Check(_vk.AllocateDescriptorSets(_device, in allocateInfo, out var descriptorSet),
            "vkAllocateDescriptorSets");

        if (contents.Length > 0)
        {
            var bufferInfos = stackalloc DescriptorBufferInfo[contents.Length];
            for (var i = 0; i < contents.Length; i++)
            {
                bufferInfos[i] = new DescriptorBufferInfo
                {
                    Buffer = _buffers[i].Buffer,
                    Offset = 0,
                    Range = (ulong)_buffers[i].Size,
                };
            }

            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = descriptorSet,
                DstBinding = 0,
                DescriptorCount = (uint)contents.Length,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = bufferInfos,
            };
            _vk.UpdateDescriptorSets(_device, 1, in write, 0, null);
        }

        const Format format = Format.R8G8B8A8Unorm;
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
            InitialLayout = ImageLayout.Undefined,
        };
        Check(_vk.CreateImage(_device, in imageInfo, null, out var colorImage), "vkCreateImage");
        _vk.GetImageMemoryRequirements(_device, colorImage, out var imageRequirements);
        var imageAllocate = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = imageRequirements.Size,
            MemoryTypeIndex = FindMemoryType(
                imageRequirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        Check(_vk.AllocateMemory(_device, in imageAllocate, null, out var imageMemory), "vkAllocateMemory");
        Check(_vk.BindImageMemory(_device, colorImage, imageMemory, 0), "vkBindImageMemory");

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = colorImage,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        Check(_vk.CreateImageView(_device, in viewInfo, null, out var colorView), "vkCreateImageView");

        var attachment = new AttachmentDescription
        {
            Format = format,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.TransferSrcOptimal,
        };
        var colorRef = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef,
        };
        var renderPassInfo = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &attachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
        };
        Check(_vk.CreateRenderPass(_device, in renderPassInfo, null, out var renderPass),
            "vkCreateRenderPass");

        var framebufferInfo = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = renderPass,
            AttachmentCount = 1,
            PAttachments = &colorView,
            Width = width,
            Height = height,
            Layers = 1,
        };
        Check(_vk.CreateFramebuffer(_device, in framebufferInfo, null, out var framebuffer),
            "vkCreateFramebuffer");

        var vertexModule = CreateModule(vertexSpirv);
        var fragmentModule = CreateModule(fragmentSpirv);

        var pushRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.FragmentBit,
            Offset = 0,
            Size = 12,
        };
        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange,
        };
        Check(_vk.CreatePipelineLayout(_device, in pipelineLayoutInfo, null, out var pipelineLayout),
            "vkCreatePipelineLayout");

        var entry = (byte*)SilkMarshal.StringToPtr("main");
        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = vertexModule,
            PName = entry,
        };
        stages[1] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = fragmentModule,
            PName = entry,
        };

        var vertexInput = new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
        };
        var inputAssembly = new PipelineInputAssemblyStateCreateInfo
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = PrimitiveTopology.TriangleList,
        };
        var viewport = new Viewport(0, 0, width, height, 0, 1);
        var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D(width, height));
        var viewportState = new PipelineViewportStateCreateInfo
        {
            SType = StructureType.PipelineViewportStateCreateInfo,
            ViewportCount = 1,
            PViewports = &viewport,
            ScissorCount = 1,
            PScissors = &scissor,
        };
        var rasterizer = new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            PolygonMode = PolygonMode.Fill,
            CullMode = CullModeFlags.None,
            FrontFace = FrontFace.CounterClockwise,
            LineWidth = 1f,
        };
        var multisample = new PipelineMultisampleStateCreateInfo
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            RasterizationSamples = SampleCountFlags.Count1Bit,
        };
        var blendAttachment = new PipelineColorBlendAttachmentState
        {
            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                ColorComponentFlags.BBit | ColorComponentFlags.ABit,
        };
        var blend = new PipelineColorBlendStateCreateInfo
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            AttachmentCount = 1,
            PAttachments = &blendAttachment,
        };

        var graphicsInfo = new GraphicsPipelineCreateInfo
        {
            SType = StructureType.GraphicsPipelineCreateInfo,
            StageCount = 2,
            PStages = stages,
            PVertexInputState = &vertexInput,
            PInputAssemblyState = &inputAssembly,
            PViewportState = &viewportState,
            PRasterizationState = &rasterizer,
            PMultisampleState = &multisample,
            PColorBlendState = &blend,
            Layout = pipelineLayout,
            RenderPass = renderPass,
            Subpass = 0,
        };
        var pipelineResult = _vk.CreateGraphicsPipelines(
            _device, default, 1, in graphicsInfo, null, out var pipeline);
        SilkMarshal.Free((nint)entry);
        Check(pipelineResult, "vkCreateGraphicsPipelines");

        var readback = new byte[width * height * 4];
        CreateBuffer(readback);
        var readbackBuffer = _buffers[^1].Buffer;

        var commandInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        Check(_vk.AllocateCommandBuffers(_device, in commandInfo, out var commandBuffer),
            "vkAllocateCommandBuffers");
        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        Check(_vk.BeginCommandBuffer(commandBuffer, in beginInfo), "vkBeginCommandBuffer");

        var clear = new ClearValue(new ClearColorValue(0, 0, 0, 1));
        var passBegin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = renderPass,
            Framebuffer = framebuffer,
            RenderArea = scissor,
            ClearValueCount = 1,
            PClearValues = &clear,
        };
        _vk.CmdBeginRenderPass(commandBuffer, in passBegin, SubpassContents.Inline);
        _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, pipeline);
        _vk.CmdBindDescriptorSets(
            commandBuffer, PipelineBindPoint.Graphics, pipelineLayout, 0, 1, in descriptorSet, 0, null);
        var limits = stackalloc uint[3];
        limits[0] = width;
        limits[1] = height;
        limits[2] = 1;
        _vk.CmdPushConstants(
            commandBuffer, pipelineLayout, ShaderStageFlags.FragmentBit, 0, 12, limits);
        _vk.CmdDraw(commandBuffer, 3, 1, 0, 0);
        _vk.CmdEndRenderPass(commandBuffer);

        var copy = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D(width, height, 1),
        };
        _vk.CmdCopyImageToBuffer(
            commandBuffer, colorImage, ImageLayout.TransferSrcOptimal, readbackBuffer, 1, in copy);
        Check(_vk.EndCommandBuffer(commandBuffer), "vkEndCommandBuffer");

        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
        };
        Check(_vk.QueueSubmit(_queue, 1, in submit, default), "vkQueueSubmit");
        Check(_vk.QueueWaitIdle(_queue), "vkQueueWaitIdle");

        return Download(_buffers.Count - 1);
    }

    /// <summary>
    /// their evaluations resolved, and the vertex count the resource block
    /// implies (<c>6 × numParticles</c> — the vertex program expands each
    /// particle into an inline quad).
    /// </summary>
    /// <summary>
    /// One sampled texture, already untiled to a linear layout.
    /// </summary>
    internal sealed record GuestImage(byte[] Pixels, uint Width, uint Height, Format Format);

    /// <param name="BufferAlias">Per slot, the index of an earlier slot whose
    /// GPU buffer this slot shares, or -1 for its own. The two stages address
    /// one guest allocation through separate descriptor slots — particle_vv
    /// latches <c>renLife</c> into the record and particle_p reads it back, so
    /// giving each stage its own copy loses the write.</param>
    /// <param name="Additive">False for the base plate, which replaces the
    /// clear; true for the particle groups, which accumulate over it.</param>
    internal readonly record struct ParticleDraw(
        byte[] VertexSpirv,
        byte[] FragmentSpirv,
        byte[][] Buffers,
        uint VertexCount,
        int[]? BufferAlias = null,
        bool Additive = true,
        IReadOnlyList<GuestImage>? Images = null,
        uint InstanceCount = 1,
        PrimitiveTopology Topology = PrimitiveTopology.TriangleList);

    /// <summary>
    /// Renders every particle group of one frame into a single image.
    ///
    /// <para>The groups composite <b>additively</b>: <c>particle_p</c> exports
    /// alpha zero and the console draws the field with a `ONE/ONE` blend, so
    /// the passes accumulate rather than overwrite. All of them share one
    /// render pass; a per-group pass with its own clear would throw away every
    /// group but the last.</para>
    /// </summary>
    internal byte[] RenderParticleFrame(
        IReadOnlyList<ParticleDraw> draws, uint width, uint height)
        => RenderParticleFrame(draws, width, height, out _);

    /// <summary>
    /// As above, and also hands back what the draws left in their storage
    /// buffers. <c>particle_vv</c> stores the <c>renLife</c> latch back into
    /// the record for corner 0, so the readback is the only direct evidence
    /// that the vertex stage ran at all when nothing rasterises.
    /// </summary>
    internal byte[] RenderParticleFrame(
        IReadOnlyList<ParticleDraw> draws, uint width, uint height, out byte[][][] buffersAfter)
    {
        ReleaseBuffers();
        var uploads = new List<(Image Image, DeviceMemory Memory, ImageView View, Sampler Sampler)>();

        const Format format = Format.R8G8B8A8Unorm;
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
            InitialLayout = ImageLayout.Undefined,
        };
        Check(_vk.CreateImage(_device, in imageInfo, null, out var colorImage), "vkCreateImage");
        _vk.GetImageMemoryRequirements(_device, colorImage, out var imageRequirements);
        var imageAllocate = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = imageRequirements.Size,
            MemoryTypeIndex = FindMemoryType(
                imageRequirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        Check(_vk.AllocateMemory(_device, in imageAllocate, null, out var imageMemory), "vkAllocateMemory");
        Check(_vk.BindImageMemory(_device, colorImage, imageMemory, 0), "vkBindImageMemory");

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = colorImage,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        Check(_vk.CreateImageView(_device, in viewInfo, null, out var colorView), "vkCreateImageView");

        var attachment = new AttachmentDescription
        {
            Format = format,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.TransferSrcOptimal,
        };
        var colorRef = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef,
        };
        var renderPassInfo = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &attachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
        };
        Check(_vk.CreateRenderPass(_device, in renderPassInfo, null, out var renderPass),
            "vkCreateRenderPass");

        var framebufferInfo = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = renderPass,
            AttachmentCount = 1,
            PAttachments = &colorView,
            Width = width,
            Height = height,
            Layers = 1,
        };
        Check(_vk.CreateFramebuffer(_device, in framebufferInfo, null, out var framebuffer),
            "vkCreateFramebuffer");

        var viewport = new Viewport(0, 0, width, height, 0, 1);
        var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D(width, height));

        var pipelines = new Pipeline[draws.Count];
        var layouts = new PipelineLayout[draws.Count];
        var sets = new DescriptorSet[draws.Count];
        var setLayouts = new DescriptorSetLayout[draws.Count];
        var pools = new DescriptorPool[draws.Count];
        var modules = new List<ShaderModule>();
        var entry = (byte*)SilkMarshal.StringToPtr("main");

        for (var d = 0; d < draws.Count; d++)
        {
            var draw = draws[d];
            var first = _buffers.Count;
            var slots = new int[draw.Buffers.Length];
            for (var i = 0; i < draw.Buffers.Length; i++)
            {
                var alias = draw.BufferAlias is { } map && map[i] >= 0 ? map[i] : -1;
                if (alias >= 0)
                {
                    slots[i] = slots[alias];
                    continue;
                }

                CreateBuffer(draw.Buffers[i]);
                slots[i] = _buffers.Count - 1;
            }

            // Binding 0 is the guest storage-buffer array; the translator puts
            // sampled images at binding index+1 on the same set.
            var images = draw.Images ?? [];
            var layoutBindings = stackalloc DescriptorSetLayoutBinding[1 + images.Count];
            layoutBindings[0] = new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = (uint)Math.Max(1, draw.Buffers.Length),
                StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            };
            for (var k = 0; k < images.Count; k++)
            {
                layoutBindings[k + 1] = new DescriptorSetLayoutBinding
                {
                    Binding = (uint)(k + 1),
                    DescriptorType = DescriptorType.CombinedImageSampler,
                    DescriptorCount = 1,
                    StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                };
            }

            var layoutInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = (uint)(1 + images.Count),
                PBindings = layoutBindings,
            };
            Check(_vk.CreateDescriptorSetLayout(_device, in layoutInfo, null, out setLayouts[d]),
                "vkCreateDescriptorSetLayout");

            var poolSizes = stackalloc DescriptorPoolSize[2];
            poolSizes[0] = new DescriptorPoolSize
            {
                Type = DescriptorType.StorageBuffer,
                DescriptorCount = (uint)Math.Max(1, draw.Buffers.Length),
            };
            poolSizes[1] = new DescriptorPoolSize
            {
                Type = DescriptorType.CombinedImageSampler,
                DescriptorCount = (uint)Math.Max(1, images.Count),
            };
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = 1,
                PoolSizeCount = images.Count > 0 ? 2u : 1u,
                PPoolSizes = poolSizes,
            };
            Check(_vk.CreateDescriptorPool(_device, in poolInfo, null, out pools[d]),
                "vkCreateDescriptorPool");

            fixed (DescriptorSetLayout* setLayoutPtr = &setLayouts[d])
            {
                var allocateInfo = new DescriptorSetAllocateInfo
                {
                    SType = StructureType.DescriptorSetAllocateInfo,
                    DescriptorPool = pools[d],
                    DescriptorSetCount = 1,
                    PSetLayouts = setLayoutPtr,
                };
                Check(_vk.AllocateDescriptorSets(_device, in allocateInfo, out sets[d]),
                    "vkAllocateDescriptorSets");
            }

            if (draw.Buffers.Length > 0)
            {
                var bufferInfos = stackalloc DescriptorBufferInfo[draw.Buffers.Length];
                for (var i = 0; i < draw.Buffers.Length; i++)
                {
                    bufferInfos[i] = new DescriptorBufferInfo
                    {
                        Buffer = _buffers[slots[i]].Buffer,
                        Offset = 0,
                        Range = (ulong)_buffers[slots[i]].Size,
                    };
                }

                var write = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = sets[d],
                    DstBinding = 0,
                    DescriptorCount = (uint)draw.Buffers.Length,
                    DescriptorType = DescriptorType.StorageBuffer,
                    PBufferInfo = bufferInfos,
                };
                _vk.UpdateDescriptorSets(_device, 1, in write, 0, null);
            }

            for (var k = 0; k < images.Count; k++)
            {
                var (view, sampler) = CreateSampledImage(images[k], uploads);
                var imageInfoWrite = new DescriptorImageInfo
                {
                    Sampler = sampler,
                    ImageView = view,
                    ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
                };
                var imageWrite = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = sets[d],
                    DstBinding = (uint)(k + 1),
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.CombinedImageSampler,
                    PImageInfo = &imageInfoWrite,
                };
                _vk.UpdateDescriptorSets(_device, 1, in imageWrite, 0, null);
            }

            var pushRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                Offset = 0,
                Size = 12,
            };
            fixed (DescriptorSetLayout* setLayoutPtr = &setLayouts[d])
            {
                var pipelineLayoutInfo = new PipelineLayoutCreateInfo
                {
                    SType = StructureType.PipelineLayoutCreateInfo,
                    SetLayoutCount = 1,
                    PSetLayouts = setLayoutPtr,
                    PushConstantRangeCount = 1,
                    PPushConstantRanges = &pushRange,
                };
                Check(_vk.CreatePipelineLayout(_device, in pipelineLayoutInfo, null, out layouts[d]),
                    "vkCreatePipelineLayout");
            }

            var vertexModule = CreateModule(draw.VertexSpirv);
            var fragmentModule = CreateModule(draw.FragmentSpirv);
            modules.Add(vertexModule);
            modules.Add(fragmentModule);

            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = vertexModule,
                PName = entry,
            };
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = fragmentModule,
                PName = entry,
            };

            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
            };
            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = Environment.GetEnvironmentVariable("DEBUG_TOPOLOGY") == "points"
                    ? PrimitiveTopology.PointList
                    : draw.Topology,
            };
            var viewportState = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                PViewports = &viewport,
                ScissorCount = 1,
                PScissors = &scissor,
            };
            var rasterizer = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
                LineWidth = 1f,
            };
            var multisample = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
            };
            var blendAttachment = new PipelineColorBlendAttachmentState
            {
                BlendEnable = draw.Additive &&
                    Environment.GetEnvironmentVariable("DEBUG_NOBLEND") != "1",
                SrcColorBlendFactor = BlendFactor.One,
                DstColorBlendFactor = BlendFactor.One,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.One,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                    ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            };
            var blend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 1,
                PAttachments = &blendAttachment,
            };

            var graphicsInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewportState,
                PRasterizationState = &rasterizer,
                PMultisampleState = &multisample,
                PColorBlendState = &blend,
                Layout = layouts[d],
                RenderPass = renderPass,
                Subpass = 0,
            };
            Check(
                _vk.CreateGraphicsPipelines(_device, default, 1, in graphicsInfo, null, out pipelines[d]),
                "vkCreateGraphicsPipelines");
        }

        SilkMarshal.Free((nint)entry);

        var readback = new byte[width * height * 4];
        CreateBuffer(readback);
        var readbackIndex = _buffers.Count - 1;
        var readbackBuffer = _buffers[readbackIndex].Buffer;

        var commandInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        Check(_vk.AllocateCommandBuffers(_device, in commandInfo, out var commandBuffer),
            "vkAllocateCommandBuffers");
        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        Check(_vk.BeginCommandBuffer(commandBuffer, in beginInfo), "vkBeginCommandBuffer");

        var clear = new ClearValue(new ClearColorValue(0, 0, 0, 1));
        var passBegin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = renderPass,
            Framebuffer = framebuffer,
            RenderArea = scissor,
            ClearValueCount = 1,
            PClearValues = &clear,
        };
        _vk.CmdBeginRenderPass(commandBuffer, in passBegin, SubpassContents.Inline);
        var limits = stackalloc uint[3];
        limits[1] = 1;
        limits[2] = 1;
        for (var d = 0; d < draws.Count; d++)
        {
            _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, pipelines[d]);
            _vk.CmdBindDescriptorSets(
                commandBuffer, PipelineBindPoint.Graphics, layouts[d], 0, 1, in sets[d], 0, null);
            limits[0] = draws[d].VertexCount;
            _vk.CmdPushConstants(
                commandBuffer,
                layouts[d],
                ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                0,
                12,
                limits);
            _vk.CmdDraw(commandBuffer, draws[d].VertexCount, draws[d].InstanceCount, 0, 0);
        }

        _vk.CmdEndRenderPass(commandBuffer);

        var copy = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D(width, height, 1),
        };
        _vk.CmdCopyImageToBuffer(
            commandBuffer, colorImage, ImageLayout.TransferSrcOptimal, readbackBuffer, 1, in copy);
        Check(_vk.EndCommandBuffer(commandBuffer), "vkEndCommandBuffer");

        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
        };
        Check(_vk.QueueSubmit(_queue, 1, in submit, default), "vkQueueSubmit");
        Check(_vk.QueueWaitIdle(_queue), "vkQueueWaitIdle");

        var image = Download(readbackIndex);

        buffersAfter = new byte[draws.Count][][];
        var cursor = 0;
        for (var d = 0; d < draws.Count; d++)
        {
            buffersAfter[d] = new byte[draws[d].Buffers.Length][];
            for (var i = 0; i < draws[d].Buffers.Length; i++)
            {
                var alias = draws[d].BufferAlias is { } map && map[i] >= 0 ? map[i] : -1;
                buffersAfter[d][i] = alias >= 0 ? buffersAfter[d][alias] : Download(cursor++);
            }
        }

        _vk.FreeCommandBuffers(_device, _commandPool, 1, in commandBuffer);

        for (var d = 0; d < draws.Count; d++)
        {
            _vk.DestroyPipeline(_device, pipelines[d], null);
            _vk.DestroyPipelineLayout(_device, layouts[d], null);
            _vk.DestroyDescriptorPool(_device, pools[d], null);
            _vk.DestroyDescriptorSetLayout(_device, setLayouts[d], null);
        }

        foreach (var module in modules)
        {
            _vk.DestroyShaderModule(_device, module, null);
        }

        foreach (var (guestImage, guestMemory, guestView, guestSampler) in uploads)
        {
            _vk.DestroySampler(_device, guestSampler, null);
            _vk.DestroyImageView(_device, guestView, null);
            _vk.DestroyImage(_device, guestImage, null);
            _vk.FreeMemory(_device, guestMemory, null);
        }

        _vk.DestroyFramebuffer(_device, framebuffer, null);
        _vk.DestroyRenderPass(_device, renderPass, null);
        _vk.DestroyImageView(_device, colorView, null);
        _vk.DestroyImage(_device, colorImage, null);
        _vk.FreeMemory(_device, imageMemory, null);
        ReleaseBuffers();
        return image;
    }

    /// <summary>
    /// Uploads one guest texture and returns a view and sampler for it.
    ///
    /// <para>Sampled images are the capability this runner never had, and they
    /// are what the light layer, the large particles and the blur passes all
    /// wait on. The upload is a staging buffer plus two layout transitions —
    /// nothing clever — because the interesting part is upstream: the pixels
    /// </summary>
    private (ImageView View, Sampler Sampler) CreateSampledImage(
        GuestImage guest,
        List<(Image, DeviceMemory, ImageView, Sampler)> owned)
    {
        var info = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = guest.Format,
            Extent = new Extent3D(guest.Width, guest.Height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
            InitialLayout = ImageLayout.Undefined,
        };
        Check(_vk.CreateImage(_device, in info, null, out var image), "vkCreateImage(guest)");
        _vk.GetImageMemoryRequirements(_device, image, out var requirements);
        var allocate = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = FindMemoryType(
                requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        Check(_vk.AllocateMemory(_device, in allocate, null, out var memory), "vkAllocateMemory(guest)");
        Check(_vk.BindImageMemory(_device, image, memory, 0), "vkBindImageMemory(guest)");

        CreateBuffer(guest.Pixels);
        var staging = _buffers[^1].Buffer;

        var commandInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        Check(_vk.AllocateCommandBuffers(_device, in commandInfo, out var command), "vkAllocateCommandBuffers");
        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        Check(_vk.BeginCommandBuffer(command, in begin), "vkBeginCommandBuffer(guest)");

        var toTransfer = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.TransferDstOptimal,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            DstAccessMask = AccessFlags.TransferWriteBit,
        };
        _vk.CmdPipelineBarrier(
            command, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit,
            0, 0, null, 0, null, 1, in toTransfer);

        var copy = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D(guest.Width, guest.Height, 1),
        };
        _vk.CmdCopyBufferToImage(
            command, staging, image, ImageLayout.TransferDstOptimal, 1, in copy);

        var toRead = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.TransferDstOptimal,
            NewLayout = ImageLayout.ShaderReadOnlyOptimal,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit,
        };
        _vk.CmdPipelineBarrier(
            command, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit,
            0, 0, null, 0, null, 1, in toRead);
        Check(_vk.EndCommandBuffer(command), "vkEndCommandBuffer(guest)");

        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &command,
        };
        Check(_vk.QueueSubmit(_queue, 1, in submit, default), "vkQueueSubmit(guest)");
        Check(_vk.QueueWaitIdle(_queue), "vkQueueWaitIdle(guest)");
        _vk.FreeCommandBuffers(_device, _commandPool, 1, in command);

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = guest.Format,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        Check(_vk.CreateImageView(_device, in viewInfo, null, out var view), "vkCreateImageView(guest)");

        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MaxLod = 1f,
        };
        Check(_vk.CreateSampler(_device, in samplerInfo, null, out var sampler), "vkCreateSampler(guest)");

        owned.Add((image, memory, view, sampler));
        return (view, sampler);
    }

    private void ReleaseBuffers()
    {
        foreach (var (buffer, memory, _) in _buffers)
        {
            _vk.DestroyBuffer(_device, buffer, null);
            _vk.FreeMemory(_device, memory, null);
        }

        _buffers.Clear();
    }

    private ShaderModule CreateModule(byte[] code)
    {
        fixed (byte* p = code)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode = (uint*)p,
            };
            Check(_vk.CreateShaderModule(_device, in info, null, out var module), "vkCreateShaderModule");
            return module;
        }
    }

    private void CreateBuffer(byte[] data)
    {
        var info = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = (ulong)data.Length,
            Usage = BufferUsageFlags.StorageBufferBit,
            SharingMode = SharingMode.Exclusive,
        };
        Check(_vk.CreateBuffer(_device, in info, null, out var buffer), "vkCreateBuffer");
        _vk.GetBufferMemoryRequirements(_device, buffer, out var requirements);
        var allocate = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = FindMemoryType(
                requirements.MemoryTypeBits,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit),
        };
        Check(_vk.AllocateMemory(_device, in allocate, null, out var memory), "vkAllocateMemory");
        Check(_vk.BindBufferMemory(_device, buffer, memory, 0), "vkBindBufferMemory");

        void* mapped;
        Check(_vk.MapMemory(_device, memory, 0, (ulong)data.Length, 0, &mapped), "vkMapMemory");
        data.AsSpan().CopyTo(new Span<byte>(mapped, data.Length));
        _vk.UnmapMemory(_device, memory);

        _buffers.Add((buffer, memory, data.Length));
    }

    private byte[] Download(int index)
    {
        var (_, memory, size) = _buffers[index];
        var result = new byte[size];
        void* mapped;
        Check(_vk.MapMemory(_device, memory, 0, (ulong)size, 0, &mapped), "vkMapMemory");
        new Span<byte>(mapped, size).CopyTo(result);
        _vk.UnmapMemory(_device, memory);
        return result;
    }

    private uint FindMemoryType(uint filter, MemoryPropertyFlags flags)
    {
        _vk.GetPhysicalDeviceMemoryProperties(Physical, out var properties);
        for (var i = 0u; i < properties.MemoryTypeCount; i++)
        {
            if ((filter & (1u << (int)i)) != 0 &&
                (properties.MemoryTypes[(int)i].PropertyFlags & flags) == flags)
            {
                return i;
            }
        }

        throw new InvalidOperationException("no suitable memory type");
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
        foreach (var (buffer, memory, _) in _buffers)
        {
            _vk.DestroyBuffer(_device, buffer, null);
            _vk.FreeMemory(_device, memory, null);
        }

        _vk.DestroyCommandPool(_device, _commandPool, null);
        _vk.DestroyDevice(_device, null);
        _vk.DestroyInstance(_instance, null);
        _vk.Dispose();
    }
}
