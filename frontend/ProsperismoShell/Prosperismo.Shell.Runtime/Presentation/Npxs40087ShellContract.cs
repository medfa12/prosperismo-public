// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using System.Runtime.InteropServices;

namespace Prosperismo.Libs.Presentation;

/// <summary>The strength and owner of a value in the recovered shell contract.</summary>
public enum SonyShellEvidenceClass
{
    FirmwareConfirmed,
    ConsoleVideoMeasured,
    CommunityFigmaMeasured,
    HostAssumption,
}

/// <summary>Traceable provenance for one part of the recovered shell contract.</summary>
public readonly record struct SonyShellEvidence(
    SonyShellEvidenceClass Class,
    string Owner,
    string Artifact,
    string Locator);

public enum Npxs40087HostTopology
{
    TriangleList,
    TriangleStrip,
}

public readonly record struct Npxs40087Triangle(int A, int B, int C);

/// <summary>A guest draw's required host-side connectivity.</summary>
public readonly record struct Npxs40087DrawTopologyContract(
    string Name,
    long ShaderFileOffset,
    string ShaderSha256,
    uint? GuestPrimitiveType,
    int GuestSubmittedVerticesPerElement,
    Npxs40087HostTopology HostTopology,
    int HostVerticesPerElement,
    bool Indexed,
    bool RequiresHostVertexExpansion,
    Npxs40087Triangle FirstTriangle,
    Npxs40087Triangle SecondTriangle,
    SonyShellEvidence Evidence)
{
    public bool IsCompatibleHostDraw(
        Npxs40087HostTopology topology,
        int verticesPerElement,
        bool indexed) =>
        topology == HostTopology &&
        verticesPerElement == HostVerticesPerElement &&
        indexed == Indexed;
}

/// <summary>The exact 0x7c-byte light colour constant buffer.</summary>
public readonly record struct Npxs40087ColorCbContract(
    int SeederPreset,
    Vector4 LightColor,
    Vector4 LightColorOnFloor,
    Vector4 Light2Color,
    Vector4 Light2ColorOnFloor,
    Vector4 PointLightColor,
    Vector4 PointLightAmbientColor,
    Vector4 ThemedColor,
    float Gamma,
    float GlobalIntensity,
    float NoiseIntensity,
    string AssetSha256,
    SonyShellEvidence Evidence)
{
    public const int ByteCount = 0x7c;

    public byte[] ToBytes()
    {
        Span<float> values = stackalloc float[ByteCount / sizeof(float)];
        var offset = 0;
        Write(values, ref offset, LightColor);
        Write(values, ref offset, LightColorOnFloor);
        Write(values, ref offset, Light2Color);
        Write(values, ref offset, Light2ColorOnFloor);
        Write(values, ref offset, PointLightColor);
        Write(values, ref offset, PointLightAmbientColor);
        Write(values, ref offset, ThemedColor);
        values[offset++] = Gamma;
        values[offset++] = GlobalIntensity;
        values[offset] = NoiseIntensity;
        return MemoryMarshal.AsBytes(values).ToArray();

        static void Write(Span<float> destination, ref int index, Vector4 value)
        {
            destination[index++] = value.X;
            destination[index++] = value.Y;
            destination[index++] = value.Z;
            destination[index++] = value.W;
        }
    }
}

public enum Npxs40087ParticlePlacementModel
{
    NativeThreeDimensionalSimulation,
}

/// <summary>Native selector and lifecycle facts for cold boot through retained HOME.</summary>
public readonly record struct Npxs40087AmbientContract(
    int ColdBootSelector,
    int AmbientSelector,
    double ManagedColdBootSeconds,
    double FirmwareInitialLightClockSeconds,
    double PresentationLightClockOriginSeconds,
    double LightPaletteTransitionSeconds,
    double ManagedPatternActionSeconds,
    double AuthoredPatternActionSeconds,
    double ManagedHomeLightTransitionSeconds,
    double AuthoredPatternActionEndSeconds,
    double AuthoredSelectorTransitionSeconds,
    double AuthoredPreviousInstanceReleaseSeconds,
    int PropertyRecordCount,
    int PropertyRecordStride,
    int LogicalBankCount,
    int SmallGroupsInAmbient,
    int LargeGroupsInAmbient,
    int LargeGroupsInColdBoot,
    int ResourceStepHertz,
    bool WrapsOrRestarts,
    Npxs40087ParticlePlacementModel PlacementModel,
    int? ConfirmedSmallParticleColorPatternFlag,
    int CurrentHostColorPatternFlagAssumption,
    SonyShellEvidence FirmwareEvidence,
    SonyShellEvidence ConsoleValidationEvidence,
    SonyShellEvidence ColorPatternAssumptionEvidence);

/// <summary>
/// Game Hub geometry recovered from NPXS40033. It is colocated here because
/// it is a shell contract consumed alongside NPXS40087, not because the
/// background application owns the Game Hub.
/// </summary>
public readonly record struct Npxs40033GameHubContract(
    int DesignWidth,
    int CtaContainerLeft,
    int CtaContainerTop,
    int CtaContainerWidth,
    int CtaContainerHeight,
    int ButtonHeight,
    int ButtonGap,
    int CondensedButtonWidth,
    int MaximumVisibleOrdinaryActions,
    int LogoMaximumWidth,
    int LogoMaximumHeight,
    int LogoBottomMargin,
    bool LogoPreservesAspectRatio,
    int DisplayNameFallbackMaximumLines,
    SonyShellEvidence BundleEvidence,
    SonyShellEvidence PositionEvidence)
{
    public int PrimaryButtonWidthWithOverflow =>
        CtaContainerWidth - ButtonGap - CondensedButtonWidth;

    public bool HasOverflow(int availableActionCount) =>
        availableActionCount > MaximumVisibleOrdinaryActions;
}

/// <summary>
/// Lower-tier community redraw measurements retained only as a comparison.
/// They must not override <see cref="Npxs40033GameHubContract"/>.
/// </summary>
public readonly record struct Npxs40033GameHubFigmaReference(
    int LogoLeft,
    int LogoTop,
    int LogoWidth,
    int LogoHeight,
    int PlayLeft,
    int PlayTop,
    int PlayWidth,
    int PlayHeight,
    int MoreLeft,
    int MoreTop,
    int MoreWidth,
    int MoreHeight,
    int ButtonGap,
    SonyShellEvidence Evidence);

/// <summary>
/// Central, typed recovery contract for the native 12.40 shell evidence.
/// Consumers must not silently promote measured references or assumptions to
/// </summary>
public static class Npxs40087ShellContract
{
    public const int NggPrimitiveExportTarget = 20;

    public const string Npxs40087EbootSha256 =
        "18c9320be767a540578e54cb769f94996c3f37a4f158ef977ebfb798ffd6b04f";
    public const string Npxs40033BundleSha256 =
        "2ef71385793a028a834dd196b0e7918a5229a251b7d3cc0b3dfe494e421164a0";
    public const string BootLoginVideoSha256 =
        "ed80c70e9e560c48a620194458716f33462384b4e57d23b59687a46184adc703";
    public const string HomeMenuTourVideoSha256 =
        "d4726f1fa7814ccb644da8046abbaf7abe26b845263a6408b06608a365816255";
    public const string CommunityFigmaSha256 =
        "a0349c3bfc4611f8e7b407e85cf06bee3fde3f8bb783f6fe9bb8624e53e6de20";

    private static readonly SonyShellEvidence Firmware1240 = new(
        SonyShellEvidenceClass.FirmwareConfirmed,
        "NPXS40087",
        $"12.40 eboot.bin sha256:{Npxs40087EbootSha256}",
        "eboot+0xEA290 and embedded shader slices");

    public static readonly Npxs40087DrawTopologyContract LightRectangle = new(
        Name: "rect_uv_vv light compositor rectangle",
        ShaderFileOffset: 0x11eee00,
        ShaderSha256: "3dff78d60fe00e4cf542fefecbf0e2ebd39ab81cfc51fe3335c98448ec516bcf",
        GuestPrimitiveType: 6,
        GuestSubmittedVerticesPerElement: 3,
        HostTopology: Npxs40087HostTopology.TriangleStrip,
        HostVerticesPerElement: 4,
        Indexed: false,
        RequiresHostVertexExpansion: true,
        FirstTriangle: new(0, 1, 2),
        SecondTriangle: new(2, 1, 3),
        Evidence: Firmware1240);

    public static readonly Npxs40087DrawTopologyContract SmallParticle = new(
        Name: "particle_vv billboard",
        ShaderFileOffset: 0x1201d00,
        ShaderSha256: "2c27623d512217c614270f8b6396ccf4a84fb53d6d04481cb2229da861c9959e",
        GuestPrimitiveType: null,
        GuestSubmittedVerticesPerElement: 6,
        HostTopology: Npxs40087HostTopology.TriangleList,
        HostVerticesPerElement: 6,
        Indexed: false,
        RequiresHostVertexExpansion: false,
        FirstTriangle: new(0, 1, 2),
        SecondTriangle: new(3, 4, 5),
        Evidence: Firmware1240);

    public static readonly Npxs40087DrawTopologyContract LargeParticle = new(
        Name: "large_particle_vv billboard",
        ShaderFileOffset: 0x1202c00,
        ShaderSha256: "952ddd2d21cc2188d11dfeb793b13b7661f1d71121cff701377d3f24ca58dbf4",
        GuestPrimitiveType: null,
        GuestSubmittedVerticesPerElement: 6,
        HostTopology: Npxs40087HostTopology.TriangleList,
        HostVerticesPerElement: 6,
        Indexed: false,
        RequiresHostVertexExpansion: false,
        FirstTriangle: new(0, 1, 2),
        SecondTriangle: new(3, 4, 5),
        Evidence: Firmware1240);

    public static readonly Npxs40087ColorCbContract BootPalette = new(
        SeederPreset: 11,
        LightColor: new(0.02f, 0.02f, 0.2f, 1.0f),
        LightColorOnFloor: Vector4.One,
        Light2Color: new(0.119f, 0.119f, 0.119f, 1.0f),
        Light2ColorOnFloor: new(5.0f, 5.0f, 5.0f, 1.0f),
        PointLightColor: new(0.2f, 0.2f, 0.4f, 1.0f),
        PointLightAmbientColor: new(0.0f, 0.0f, 0.0f, 1.0f),
        ThemedColor: Vector4.One,
        Gamma: 0.454545f,
        GlobalIntensity: 0.27f,
        NoiseIntensity: 0.008f,
        AssetSha256: "cb99cbbd56044ce2533948bc8c1f249504fdae3feb2f0d73c26ea69b58c26834",
        Evidence: Firmware1240);

    public static readonly Npxs40087ColorCbContract LoginPalette = new(
        SeederPreset: 9,
        LightColor: new(0.02f, 0.02f, 0.2f, 1.0f),
        LightColorOnFloor: Vector4.One,
        Light2Color: new(0.119f, 0.119f, 0.119f, 1.0f),
        Light2ColorOnFloor: new(5.0f, 5.0f, 5.0f, 1.0f),
        PointLightColor: new(0.2f, 0.2f, 0.4f, 1.0f),
        PointLightAmbientColor: new(0.0f, 0.0f, 0.0f, 1.0f),
        ThemedColor: Vector4.One,
        Gamma: 0.454545f,
        GlobalIntensity: 0.52f,
        NoiseIntensity: 0.008f,
        AssetSha256: "0fc9e53f23407c2136b28664faf6f7554fe6a58f94604f301f265c8da5248432",
        Evidence: Firmware1240);

    public static readonly Npxs40087ColorCbContract HomePalette = new(
        SeederPreset: 4,
        LightColor: new(0.072f, 0.070f, 0.0731f, 1.0f),
        LightColorOnFloor: Vector4.One,
        Light2Color: new(0.119f, 0.119f, 0.119f, 1.0f),
        Light2ColorOnFloor: new(2.0f, 1.0f, 1.3f, 1.0f),
        PointLightColor: new(0.06255f, 0.0625f, 0.06f, 1.0f),
        PointLightAmbientColor: new(0.0375f, 0.0375f, 0.0376f, 1.0f),
        ThemedColor: Vector4.One,
        Gamma: 0.454545f,
        GlobalIntensity: 1.0f,
        NoiseIntensity: 0.008f,
        AssetSha256: "6707722e5d33ca4c86025036bc1a781ab851612bc348a936186409450969c631",
        Evidence: Firmware1240);

    public static readonly Npxs40087AmbientContract Ambient = new(
        ColdBootSelector: 0,
        AmbientSelector: 1,
        ManagedColdBootSeconds: 6.0,
        // Constructor/reset paths seed the native object's +0xCC field to 10.
        // The accepted cold-boot POC and the direct-console blue-room phase
        // align when light_p begins at zero in the authored particle-pattern
        // domain. Keep that origin separate from the recovered object seed.
        FirmwareInitialLightClockSeconds: 10.0,
        PresentationLightClockOriginSeconds: 0.0,
        // Active palette changes seed +0xE4/+0xE8 with (0, 300 ms) at
        // 0xE9C56. 0xEA030 then applies the recovered quartic ease-out.
        LightPaletteTransitionSeconds: 0.3,
        // The coldboot pattern's authored colour/particle action, not a
        // ColorCb preset switch, is mapped to this managed wall-clock point.
        ManagedPatternActionSeconds: 4.0,
        AuthoredPatternActionSeconds: 6.5,
        // The large-particle size/action event ends at authored 6.9. Under the
        // accepted piecewise clock that is managed 4.4, where the product's
        // skipped-login bridge may begin lighting the HOME room.
        ManagedHomeLightTransitionSeconds: 4.4,
        AuthoredPatternActionEndSeconds: 6.9,
        AuthoredSelectorTransitionSeconds: 8.5,
        AuthoredPreviousInstanceReleaseSeconds: 11.0,
        PropertyRecordCount: 6000,
        PropertyRecordStride: 0x44,
        LogicalBankCount: 20,
        SmallGroupsInAmbient: 8,
        LargeGroupsInAmbient: 0,
        LargeGroupsInColdBoot: 2,
        ResourceStepHertz: 60,
        WrapsOrRestarts: false,
        PlacementModel: Npxs40087ParticlePlacementModel.NativeThreeDimensionalSimulation,
        ConfirmedSmallParticleColorPatternFlag: null,
        CurrentHostColorPatternFlagAssumption: 0,
        FirmwareEvidence: Firmware1240,
        ConsoleValidationEvidence: new(
            SonyShellEvidenceClass.ConsoleVideoMeasured,
            "PS5 system software capture",
            $"PS5-Boot-login-Sequences_Media__1080p mp4.mp4 sha256:{BootLoginVideoSha256}",
            "cold-boot room near 00:09.75; blue action near 00:13.75-00:14.25; " +
            "gold-lit room near 00:14.50-00:15.00"),
        ColorPatternAssumptionEvidence: new(
            SonyShellEvidenceClass.HostAssumption,
            "Prosperismo",
            "unrecovered NPXS40087 particle_p colorPatternFlag initialization",
            "per-frame draw loops in 3.00/12.40/13.00/13.20 do not write " +
            "SRTVsPs+0x14; current renderer value remains a host assumption"));

    public static readonly Npxs40033GameHubContract GameHub = new(
        DesignWidth: 1920,
        CtaContainerLeft: 172,
        CtaContainerTop: 835,
        CtaContainerWidth: 422,
        CtaContainerHeight: 73,
        ButtonHeight: 72,
        ButtonGap: 16,
        CondensedButtonWidth: 72,
        MaximumVisibleOrdinaryActions: 1,
        LogoMaximumWidth: 720,
        LogoMaximumHeight: 148,
        LogoBottomMargin: 24,
        LogoPreservesAspectRatio: true,
        DisplayNameFallbackMaximumLines: 2,
        BundleEvidence: new(
            SonyShellEvidenceClass.FirmwareConfirmed,
            "NPXS40033",
            $"12.40 decrypted React Native bundle sha256:{Npxs40033BundleSha256}",
            "modules 470, 501, 1072, 1351, 1362"),
        PositionEvidence: new(
            SonyShellEvidenceClass.ConsoleVideoMeasured,
            "PS5 system software capture",
            $"PS5-Boot-login-Sequences_Media__1080p mp4.mp4 sha256:{BootLoginVideoSha256}",
            "1920x1080 Astro hub frame near 00:36"));

    public static readonly Npxs40033GameHubFigmaReference GameHubFigmaReference = new(
        LogoLeft: 204,
        LogoTop: 538,
        LogoWidth: 625,
        LogoHeight: 152,
        PlayLeft: 204,
        PlayTop: 817,
        PlayWidth: 371,
        PlayHeight: 78,
        MoreLeft: 590,
        MoreTop: 817,
        MoreWidth: 84,
        MoreHeight: 78,
        ButtonGap: 15,
        Evidence: new(
            SonyShellEvidenceClass.CommunityFigmaMeasured,
            "community redraw",
            $"PS5 Interactive UI (Community).fig sha256:{CommunityFigmaSha256}",
            "Home content area / Astro frame"));
}
