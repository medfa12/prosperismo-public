// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Prosperismo.ShaderCompiler.Vulkan;

public static class SpirvFixedShaders
{
    /// <summary>
    /// Full-screen stand-in for libScePsm's dedicated focus vertex programs.
    /// The original programs pack Color, St/ClipPos, and FrameSt into three
    /// parameter exports; AreaFocus and LineFocus consume that packing directly.
    /// </summary>
    public static byte[] CreateFocusFullscreenVertex()
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);

        var voidType = module.TypeVoid();
        var uintType = module.TypeInt(32, signed: false);
        var floatType = module.TypeFloat(32);
        var vec4Type = module.TypeVector(floatType, 4);
        var inputUintPointer = module.TypePointer(SpirvStorageClass.Input, uintType);
        var outputVec4Pointer = module.TypePointer(SpirvStorageClass.Output, vec4Type);

        var vertexIndex = module.AddGlobalVariable(inputUintPointer, SpirvStorageClass.Input);
        module.AddName(vertexIndex, "vertexIndex");
        module.AddDecoration(vertexIndex, SpirvDecoration.BuiltIn, (uint)SpirvBuiltIn.VertexIndex);

        var position = module.AddGlobalVariable(outputVec4Pointer, SpirvStorageClass.Output);
        module.AddName(position, "position");
        module.AddDecoration(position, SpirvDecoration.BuiltIn, (uint)SpirvBuiltIn.Position);

        var color = module.AddGlobalVariable(outputVec4Pointer, SpirvStorageClass.Output);
        var stClipPos = module.AddGlobalVariable(outputVec4Pointer, SpirvStorageClass.Output);
        var frameSt = module.AddGlobalVariable(outputVec4Pointer, SpirvStorageClass.Output);
        var attributes = new[] { color, stClipPos, frameSt };
        for (uint index = 0; index < attributes.Length; index++)
        {
            module.AddName(attributes[index], $"attr{index}");
            module.AddDecoration(attributes[index], SpirvDecoration.Location, index);
            module.AddDecoration(attributes[index], SpirvDecoration.NoPerspective);
        }

        var functionType = module.TypeFunction(voidType);
        var main = module.BeginFunction(voidType, functionType);
        module.AddName(main, "main");
        module.AddLabel();

        var indexValue = module.AddInstruction(SpirvOp.Load, uintType, vertexIndex);
        var one = module.Constant(uintType, 1);
        var two = module.Constant(uintType, 2);
        var shifted = module.AddInstruction(SpirvOp.ShiftLeftLogical, uintType, indexValue, one);
        var xBits = module.AddInstruction(SpirvOp.BitwiseAnd, uintType, shifted, two);
        var yBits = module.AddInstruction(SpirvOp.BitwiseAnd, uintType, indexValue, two);
        var x = module.AddInstruction(SpirvOp.ConvertUToF, floatType, xBits);
        var y = module.AddInstruction(SpirvOp.ConvertUToF, floatType, yBits);
        var zero = module.ConstantFloat(floatType, 0f);
        var oneFloat = module.ConstantFloat(floatType, 1f);
        var twoFloat = module.ConstantFloat(floatType, 2f);
        var frameScale = module.ConstantFloat(floatType, 16f / 9f);
        var xPosition = module.AddInstruction(SpirvOp.FMul, floatType, x, twoFloat);
        xPosition = module.AddInstruction(SpirvOp.FSub, floatType, xPosition, oneFloat);
        var yPosition = module.AddInstruction(SpirvOp.FMul, floatType, y, twoFloat);
        yPosition = module.AddInstruction(SpirvOp.FSub, floatType, yPosition, oneFloat);
        var scaledX = module.AddInstruction(SpirvOp.FMul, floatType, xPosition, frameScale);
        var scaledY = module.AddInstruction(SpirvOp.FMul, floatType, yPosition, frameScale);

        var positionValue = module.AddInstruction(
            SpirvOp.CompositeConstruct, vec4Type, xPosition, yPosition, zero, oneFloat);
        var colorValue = module.AddInstruction(
            SpirvOp.CompositeConstruct, vec4Type, oneFloat, oneFloat, oneFloat, oneFloat);
        var stClipValue = module.AddInstruction(
            SpirvOp.CompositeConstruct, vec4Type, x, y, xPosition, yPosition);
        var frameValue = module.AddInstruction(
            SpirvOp.CompositeConstruct, vec4Type, xPosition, yPosition, scaledX, scaledY);
        module.AddStatement(SpirvOp.Store, position, positionValue);
        module.AddStatement(SpirvOp.Store, color, colorValue);
        module.AddStatement(SpirvOp.Store, stClipPos, stClipValue);
        module.AddStatement(SpirvOp.Store, frameSt, frameValue);
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();

        module.AddEntryPoint(
            SpirvExecutionModel.Vertex,
            main,
            "main",
            new[] { vertexIndex, position, color, stClipPos, frameSt });
        return module.Build();
    }

    public static byte[] CreateFullscreenVertex(uint attributeCount)
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);

        var voidType = module.TypeVoid();
        var boolType = module.TypeBool();
        var uintType = module.TypeInt(32, signed: false);
        var floatType = module.TypeFloat(32);
        var vec4Type = module.TypeVector(floatType, 4);
        var inputUintPointer = module.TypePointer(SpirvStorageClass.Input, uintType);
        var outputVec4Pointer = module.TypePointer(SpirvStorageClass.Output, vec4Type);

        var vertexIndex = module.AddGlobalVariable(inputUintPointer, SpirvStorageClass.Input);
        module.AddName(vertexIndex, "vertexIndex");
        module.AddDecoration(
            vertexIndex,
            SpirvDecoration.BuiltIn,
            (uint)SpirvBuiltIn.VertexIndex);

        var position = module.AddGlobalVariable(outputVec4Pointer, SpirvStorageClass.Output);
        module.AddName(position, "position");
        module.AddDecoration(position, SpirvDecoration.BuiltIn, (uint)SpirvBuiltIn.Position);

        var attributes = new uint[attributeCount];
        for (uint index = 0; index < attributeCount; index++)
        {
            attributes[index] =
                module.AddGlobalVariable(outputVec4Pointer, SpirvStorageClass.Output);
            module.AddName(attributes[index], $"attr{index}");
            module.AddDecoration(attributes[index], SpirvDecoration.Location, index);
            module.AddDecoration(attributes[index], SpirvDecoration.NoPerspective);
        }

        var functionType = module.TypeFunction(voidType);
        var main = module.BeginFunction(voidType, functionType);
        module.AddName(main, "main");
        module.AddLabel();

        var indexValue = module.AddInstruction(SpirvOp.Load, uintType, vertexIndex);
        var one = module.Constant(uintType, 1);
        var two = module.Constant(uintType, 2);
        var shifted = module.AddInstruction(SpirvOp.ShiftLeftLogical, uintType, indexValue, one);
        var xBits = module.AddInstruction(SpirvOp.BitwiseAnd, uintType, shifted, two);
        var yBits = module.AddInstruction(SpirvOp.BitwiseAnd, uintType, indexValue, two);
        var x = module.AddInstruction(SpirvOp.ConvertUToF, floatType, xBits);
        var y = module.AddInstruction(SpirvOp.ConvertUToF, floatType, yBits);
        var zero = module.ConstantFloat(floatType, 0f);
        var oneFloat = module.ConstantFloat(floatType, 1f);
        var twoFloat = module.ConstantFloat(floatType, 2f);
        var xPosition = module.AddInstruction(SpirvOp.FMul, floatType, x, twoFloat);
        xPosition = module.AddInstruction(SpirvOp.FSub, floatType, xPosition, oneFloat);
        var yPosition = module.AddInstruction(SpirvOp.FMul, floatType, y, twoFloat);
        yPosition = module.AddInstruction(SpirvOp.FSub, floatType, yPosition, oneFloat);
        var positionValue = module.AddInstruction(
            SpirvOp.CompositeConstruct,
            vec4Type,
            xPosition,
            yPosition,
            zero,
            oneFloat);
        module.AddStatement(SpirvOp.Store, position, positionValue);

        var attributeValue = module.AddInstruction(
            SpirvOp.CompositeConstruct,
            vec4Type,
            x,
            y,
            zero,
            oneFloat);
        foreach (var attribute in attributes)
        {
            module.AddStatement(SpirvOp.Store, attribute, attributeValue);
        }

        module.AddStatement(SpirvOp.Return);
        module.EndFunction();

        var interfaces = new uint[2 + attributes.Length];
        interfaces[0] = vertexIndex;
        interfaces[1] = position;
        attributes.CopyTo(interfaces, 2);
        module.AddEntryPoint(SpirvExecutionModel.Vertex, main, "main", interfaces);
        _ = boolType;
        return module.Build();
    }

    /// <summary>
    /// Vertex shader for the NGG position-capture path: reads the clip-space
    /// vec4 the capture-compute prepass wrote for this vertex (vertex-input
    /// location 0) and passes it straight to gl_Position. The pass-through NGG
    /// export has already applied the full transform in the compute prepass, so
    /// the rasterizer just consumes those positions. The capture prepass also
    /// writes the export shader's parameter exports (<paramref
    /// name="capturedParamCount"/> vec4s at vertex-input locations 1..N); those
    /// are forwarded to the pixel shader's varyings so it shades with real
    /// values. Any attribute the pixel shader declares beyond the captured set
    /// (up to <paramref name="attributeCount"/>) is emitted as zero so the
    /// fragment inputs stay satisfied.
    /// </summary>
    public static byte[] CreatePositionPassthroughVertex(
        uint attributeCount,
        uint capturedParamCount)
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);

        var voidType = module.TypeVoid();
        var floatType = module.TypeFloat(32);
        var vec4Type = module.TypeVector(floatType, 4);
        var inputVec4Pointer = module.TypePointer(SpirvStorageClass.Input, vec4Type);
        var outputVec4Pointer = module.TypePointer(SpirvStorageClass.Output, vec4Type);

        var positionInput = module.AddGlobalVariable(inputVec4Pointer, SpirvStorageClass.Input);
        module.AddName(positionInput, "positionInput");
        module.AddDecoration(positionInput, SpirvDecoration.Location, 0u);

        var position = module.AddGlobalVariable(outputVec4Pointer, SpirvStorageClass.Output);
        module.AddName(position, "position");
        module.AddDecoration(position, SpirvDecoration.BuiltIn, (uint)SpirvBuiltIn.Position);

        // Captured parameter exports are bound at vertex-input locations 1..N
        // (location 0 is the position). One vec4 each, in export order.
        var paramInputs = new uint[capturedParamCount];
        for (uint index = 0; index < capturedParamCount; index++)
        {
            paramInputs[index] =
                module.AddGlobalVariable(inputVec4Pointer, SpirvStorageClass.Input);
            module.AddName(paramInputs[index], $"paramInput{index}");
            module.AddDecoration(paramInputs[index], SpirvDecoration.Location, index + 1u);
        }

        var outputCount = Math.Max(attributeCount, capturedParamCount);
        var attributes = new uint[outputCount];
        for (uint index = 0; index < outputCount; index++)
        {
            attributes[index] =
                module.AddGlobalVariable(outputVec4Pointer, SpirvStorageClass.Output);
            module.AddName(attributes[index], $"attr{index}");
            module.AddDecoration(attributes[index], SpirvDecoration.Location, index);
        }

        var functionType = module.TypeFunction(voidType);
        var main = module.BeginFunction(voidType, functionType);
        module.AddName(main, "main");
        module.AddLabel();

        var positionValue = module.AddInstruction(SpirvOp.Load, vec4Type, positionInput);
        module.AddStatement(SpirvOp.Store, position, positionValue);

        uint zeroVec = 0;
        if (outputCount > capturedParamCount)
        {
            var zeroFloat = module.ConstantFloat(floatType, 0f);
            zeroVec = module.AddInstruction(
                SpirvOp.CompositeConstruct,
                vec4Type,
                zeroFloat,
                zeroFloat,
                zeroFloat,
                zeroFloat);
        }

        for (uint index = 0; index < outputCount; index++)
        {
            if (index < capturedParamCount)
            {
                var paramValue = module.AddInstruction(
                    SpirvOp.Load,
                    vec4Type,
                    paramInputs[index]);
                module.AddStatement(SpirvOp.Store, attributes[index], paramValue);
            }
            else
            {
                module.AddStatement(SpirvOp.Store, attributes[index], zeroVec);
            }
        }

        module.AddStatement(SpirvOp.Return);
        module.EndFunction();

        var interfaces = new uint[2 + paramInputs.Length + attributes.Length];
        interfaces[0] = positionInput;
        interfaces[1] = position;
        paramInputs.CopyTo(interfaces, 2);
        attributes.CopyTo(interfaces, 2 + paramInputs.Length);
        module.AddEntryPoint(SpirvExecutionModel.Vertex, main, "main", interfaces);
        return module.Build();
    }

    public static byte[] CreateCopyFragment()
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);

        var voidType = module.TypeVoid();
        var floatType = module.TypeFloat(32);
        var vec2Type = module.TypeVector(floatType, 2);
        var vec4Type = module.TypeVector(floatType, 4);
        var inputVec4Pointer = module.TypePointer(SpirvStorageClass.Input, vec4Type);
        var outputVec4Pointer = module.TypePointer(SpirvStorageClass.Output, vec4Type);
        var imageType = module.TypeImage(
            floatType,
            SpirvImageDim.Dim2D,
            depth: false,
            arrayed: false,
            multisampled: false,
            sampled: 1,
            SpirvImageFormat.Unknown);
        var sampledImageType = module.TypeSampledImage(imageType);
        var sampledImagePointer =
            module.TypePointer(SpirvStorageClass.UniformConstant, sampledImageType);

        var attribute = module.AddGlobalVariable(inputVec4Pointer, SpirvStorageClass.Input);
        module.AddName(attribute, "attr0");
        module.AddDecoration(attribute, SpirvDecoration.Location, 0);

        var texture = module.AddGlobalVariable(
            sampledImagePointer,
            SpirvStorageClass.UniformConstant);
        module.AddName(texture, "tex0");
        module.AddDecoration(texture, SpirvDecoration.DescriptorSet, 0);
        module.AddDecoration(texture, SpirvDecoration.Binding, 1);

        var output = module.AddGlobalVariable(outputVec4Pointer, SpirvStorageClass.Output);
        module.AddName(output, "outColor");
        module.AddDecoration(output, SpirvDecoration.Location, 0);

        var functionType = module.TypeFunction(voidType);
        var main = module.BeginFunction(voidType, functionType);
        module.AddName(main, "main");
        module.AddLabel();

        var attributeValue = module.AddInstruction(SpirvOp.Load, vec4Type, attribute);
        var coordinates = module.AddInstruction(
            SpirvOp.VectorShuffle,
            vec2Type,
            attributeValue,
            attributeValue,
            0,
            1);
        var sampledImage = module.AddInstruction(SpirvOp.Load, sampledImageType, texture);
        var lod = module.ConstantFloat(floatType, 0f);
        var color = module.AddInstruction(
            SpirvOp.ImageSampleExplicitLod,
            vec4Type,
            sampledImage,
            coordinates,
            2,
            lod);
        module.AddStatement(SpirvOp.Store, output, color);
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();

        module.AddEntryPoint(
            SpirvExecutionModel.Fragment,
            main,
            "main",
            [attribute, texture, output]);
        module.AddExecutionMode(main, SpirvExecutionMode.OriginUpperLeft);
        return module.Build();
    }

    public static byte[] CreateSolidFragment(float red, float green, float blue, float alpha)
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);

        var voidType = module.TypeVoid();
        var floatType = module.TypeFloat(32);
        var vec4Type = module.TypeVector(floatType, 4);
        var outputVec4Pointer = module.TypePointer(SpirvStorageClass.Output, vec4Type);
        var output = module.AddGlobalVariable(outputVec4Pointer, SpirvStorageClass.Output);
        module.AddName(output, "outColor");
        module.AddDecoration(output, SpirvDecoration.Location, 0);

        var functionType = module.TypeFunction(voidType);
        var main = module.BeginFunction(voidType, functionType);
        module.AddName(main, "main");
        module.AddLabel();
        var color = module.ConstantComposite(
            vec4Type,
            module.ConstantFloat(floatType, red),
            module.ConstantFloat(floatType, green),
            module.ConstantFloat(floatType, blue),
            module.ConstantFloat(floatType, alpha));
        module.AddStatement(SpirvOp.Store, output, color);
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();

        module.AddEntryPoint(SpirvExecutionModel.Fragment, main, "main", [output]);
        module.AddExecutionMode(main, SpirvExecutionMode.OriginUpperLeft);
        return module.Build();
    }

    /// <summary>
    /// Diagnostic fragment stage that exposes one interpolated vertex output
    /// directly as color. This keeps the real guest vertex/index/depth path
    /// intact while isolating fragment-shader translation from interface data.
    /// </summary>
    public static byte[] CreateAttributeFragment(uint location)
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);

        var voidType = module.TypeVoid();
        var floatType = module.TypeFloat(32);
        var vec4Type = module.TypeVector(floatType, 4);
        var inputPointer = module.TypePointer(SpirvStorageClass.Input, vec4Type);
        var outputPointer = module.TypePointer(SpirvStorageClass.Output, vec4Type);
        var input = module.AddGlobalVariable(inputPointer, SpirvStorageClass.Input);
        module.AddName(input, $"attr{location}");
        module.AddDecoration(input, SpirvDecoration.Location, location);
        var output = module.AddGlobalVariable(outputPointer, SpirvStorageClass.Output);
        module.AddName(output, "outColor");
        module.AddDecoration(output, SpirvDecoration.Location, 0);

        var functionType = module.TypeFunction(voidType);
        var main = module.BeginFunction(voidType, functionType);
        module.AddName(main, "main");
        module.AddLabel();
        var value = module.AddInstruction(SpirvOp.Load, vec4Type, input);
        module.AddStatement(SpirvOp.Store, output, value);
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();

        module.AddEntryPoint(
            SpirvExecutionModel.Fragment,
            main,
            "main",
            [input, output]);
        module.AddExecutionMode(main, SpirvExecutionMode.OriginUpperLeft);
        return module.Build();
    }

    /// <summary>
    /// Minimal fragment stage for fixed-function depth-only passes.  The
    /// guest has no pixel shader and therefore cannot export colour; keeping
    /// this stage output-free preserves that contract while allowing Vulkan
    /// to run early/late depth tests for the translated vertex shader.
    /// </summary>
    public static byte[] CreateDepthOnlyFragment()
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);

        var voidType = module.TypeVoid();
        var functionType = module.TypeFunction(voidType);
        var main = module.BeginFunction(voidType, functionType);
        module.AddName(main, "main");
        module.AddLabel();
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();

        module.AddEntryPoint(SpirvExecutionModel.Fragment, main, "main", []);
        module.AddExecutionMode(main, SpirvExecutionMode.OriginUpperLeft);
        return module.Build();
    }
}
