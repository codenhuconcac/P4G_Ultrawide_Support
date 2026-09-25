using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using System.Numerics;
using System.Runtime.InteropServices;
using static p4gpc.ultrawidesupport.Utils;

namespace p4gpc.ultrawidesupport;

internal static unsafe class RemoveLetterbox
{
    // FUN_1404dca90 (0, 0, width, height, width, height)
    private const string PresentRenderTargetSignature =
        "4C 8B DC 55 49 8D AB F8 FE FF FF 48 81 EC 00 02 00 00 " +
        "48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 85 90 00 00 00 " +
        "49 89 5B 08 49 89 73 10 48 8B F1";

    /// <summary>
    /// FUN_1405142a0 multiplies tan(verticalFov / 2) by this value, it''s the
    /// 16x9 horizontal projection aspect, not camera state
    /// </summary>
    private const string ProjectionAspectReferenceSignature =
        "F3 0F 59 15 ?? ?? ?? ?? 0F 28 CB 48 8B 4B 08 " +
        "F3 0F 11 83 84 00 00 00 F3 0F 5E D8";

    /// <summary>
    /// FUN_1404da2c0 loads shader constants, P4G/sprite_v uses uScreen as
    /// its logical canvas size
    /// </summary>
    private const string UploadShaderConstantSignature =
        "48 89 5C 24 10 57 48 83 EC 20 48 8B D9 49 8B F9 " +
        "48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 85 DB 74 ?? 48 85 FF";

    // FUN_1404d83f0 loads and reflects vertex shader.
    private const string LoadVertexShaderSignature =
        "48 8B C4 55 41 54 41 55 41 56 41 57 48 8D A8 58 FD FF FF " +
        "48 81 EC 80 03 00 00 48 C7 45 A0 FE FF FF FF";

    /// <summary>
    /// FUN_140546b70. Shader flag bit 23 selects
    /// P4G/sprite_v.vs
    /// </summary>
    private const string RenderStuffLowSignature =
        "48 89 6C 24 18 48 89 74 24 20 57 48 83 EC 30 8B E9 " +
        "4C 89 74 24 48 48 8D 0D ?? ?? ?? ?? 4D 63 F1";

    private const string BattleOverlaySignature =
        "40 53 48 83 EC 60 48 8D 0D ?? ?? ?? ?? 48 8B DA " +
        "E8 ?? ?? ?? ?? 48 8B 83 50 15 00 00";

    private const string BattleTargetRendererSignature =
        "48 89 5C 24 20 55 56 57 48 83 EC 40 48 8B 05 ?? ?? ?? ?? " +
        "48 33 C4 48 89 44 24 38 48 8B F1 49 8B D8";

    private const string WorldToLogicalScreenCallSignature =
        "48 8D 54 24 30 F3 0F 10 8B 8C 00 00 00 48 8D 4C 24 40 " +
        "F3 0F 59 4B 2C F3 41 0F 59 CB F3 0F 58 C1 " +
        "F3 0F 11 44 24 44 E8 ?? ?? ?? ?? 85 C0 0F 84 ?? ?? ?? ?? " +
        "F3 0F 10 7C 24 30";
    private const int WorldToLogicalScreenCallOffset = 38;

    // TVListing_MainStuff (FUN_140345460)
    private const string TvListingMainSignature =
        "48 8B C4 4C 89 40 18 66 89 50 10 53 55 56 57 " +
        "41 54 41 55 41 56 41 57 48 81 EC 28 01 00 00 " +
        "0F 29 70 A8";

    /// <summary>
    /// FUN_140255760, flags 0x80019e TODO: fix the program frames since
    /// this one is the thingy that draw the frames
    /// </summary>
    private const string DrawSolidRectangleSignature =
        "48 8B C4 48 89 58 08 48 89 68 10 48 89 70 18 57 " +
        "48 81 EC D0 00 00 00 0F 29 70 E8 0F 29 78 D8";

    private const float StockAspect = 16.0f / 9.0f;
    private const float DimensionTolerance = 2.0f;
    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommit = 0x1000;
    private const uint PageGuard = 0x100;
    private const uint PageNoAccess = 0x01;
    private const int SpriteVertexStride = 24;
    private const int SpriteVertexCount = 4;
    private const uint SpriteShaderFlag = 1U << 23;
    // movie submit 0x280019e: the normal sprite bit plus this movie bit
    private const uint MovieShaderFlag = 1U << 25;
    private const float MatrixAspectTolerance = 0.025f;

    private const string SpriteVertexShaderPath = "P4G/sprite_v.vs";
    private const string EffectVertexShaderPath = "P4G/effect_v.vs";
    private const string Primitive2dVertexShaderPath = "system/vs_prim_2d_sys.vs";
    private const string Primitive2dFontVertexShaderPath = "system/vs_prim_2dFont_sys.vs";

    // mad r0.x, -cb0[0].x, 0.5, r0.y
    private static ReadOnlySpan<byte> SpriteXOriginInstruction =>
    [
        0x32, 0x00, 0x00, 0x0B, 0x12, 0x00, 0x10, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x0A, 0x80, 0x20, 0x80,
        0x41, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x01, 0x40, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x3F, 0x1A, 0x00, 0x10, 0x00,
        0x00, 0x00, 0x00, 0x00
    ];

    // mad r0.y, cb0[0].y, 0.5, -r0.z
    private static ReadOnlySpan<byte> SpriteYOriginInstruction =>
    [
        0x32, 0x00, 0x00, 0x0B, 0x22, 0x00, 0x10, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x1A, 0x80, 0x20, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x01, 0x40, 0x00, 0x00, 0x00, 0x00, 0x00, 0x3F,
        0x2A, 0x00, 0x10, 0x80, 0x41, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00
    ];

    private static readonly object AspectLock = new();
    private static readonly object UiConstantLock = new();
    private static readonly HashSet<nint> UiScreenConstants = new();

    private static IHook<PresentRenderTargetDelegate>? _presentHook;
    private static IHook<UploadShaderConstantDelegate>? _uploadShaderConstantHook;
    private static IHook<LoadVertexShaderDelegate>? _loadVertexShaderHook;
    private static IHook<RenderStuffLowDelegate>? _renderStuffLowHook;
    private static IHook<BattleOverlayDelegate>? _battleOverlayHook;
    private static IHook<BattleTargetRendererDelegate>? _battleTargetRendererHook;
    private static IHook<WorldToLogicalScreenDelegate>? _worldToLogicalScreenHook;
    private static IHook<TvListingMainDelegate>? _tvListingMainHook;
    private static IHook<DrawSolidRectangleDelegate>? _drawSolidRectangleHook;
    private static nint _uiScreenConstant;
    private static float _uiOriginalWidth;
    private static float _uiOriginalHeight;
    private static nint _projectionAspectAddress;
    private static float _requestedAspect = StockAspect;
    private static float _writtenAspect = float.NaN;
    private static float _outputAspect = StockAspect;
    private static bool _insideFinalComposite;
    private static bool _loggedActive;
    private static bool _spriteShaderPatched;
    private static bool _loggedUiCorrection;
    private static bool _loggedFullScreenQuad;
    private static bool _loggedPrimitive2dCorrection;
    private static bool _loggedEffectMatrixCorrection;
    private static bool _loggedMovieAspectFit;
    private static bool _loggedBattleProjectionCorrection;
    private static bool _loggedTvListingCorrection;
    private static bool _loggedWriteFailure;

    [ThreadStatic]
    private static bool _currentSpriteProportional;

    [ThreadStatic]
    private static bool _lastSpriteProportional;

    [ThreadStatic]
    private static bool _insideBattleOverlay;

    [ThreadStatic]
    private static bool _insideBattleTargetRenderer;

    [ThreadStatic]
    private static bool _insideTvListingMain;

    [Function(CallingConventions.Microsoft)]
    private delegate void PresentRenderTargetDelegate(
        nint renderTarget,
        float left,
        float top,
        float width,
        float height,
        float outputWidth,
        float outputHeight);

    [Function(CallingConventions.Microsoft)]
    private delegate nuint UploadShaderConstantDelegate(
        nint shaderConstant,
        nint parameter2,
        nint parameter3,
        nint sourceData);

    [Function(CallingConventions.Microsoft)]
    private delegate nint LoadVertexShaderDelegate(
        nint shaderPath,
        nint parameter2,
        nint parameter3,
        nint parameter4);

    [Function(CallingConventions.Microsoft)]
    private delegate nint RenderStuffLowDelegate(
        int primitiveType,
        uint shaderFlags,
        nint parameter3,
        int parameter4,
        int parameter5,
        nint parameter6,
        nint vertices);

    [Function(CallingConventions.Microsoft)]
    private delegate void BattleOverlayDelegate(nint parameter1, nint battlePanel);

    [Function(CallingConventions.Microsoft)]
    private delegate void BattleTargetRendererDelegate(
        nint parameter1,
        nint markerState,
        nint enemy);

    [Function(CallingConventions.Microsoft)]
    private delegate int WorldToLogicalScreenDelegate(nint worldPosition, nint screenPosition);

    [Function(CallingConventions.Microsoft)]
    private delegate void TvListingMainDelegate(
        nint animation,
        ushort channel,
        nint channelEntries);

    [Function(CallingConventions.Microsoft)]
    private delegate void DrawSolidRectangleDelegate(
        nint color,
        nint rectangle,
        float depth,
        int resetRenderState);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        internal nint BaseAddress;
        internal nint AllocationBase;
        internal uint AllocationProtect;
        internal ushort PartitionId;
        internal nuint RegionSize;
        internal uint State;
        internal uint Protect;
        internal uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtect(
        nint address,
        nuint size,
        uint newProtection,
        out uint oldProtection);

    [DllImport("kernel32.dll")]
    private static extern nuint VirtualQuery(
        nint address,
        out MemoryBasicInformation information,
        nuint informationLength);

    internal static void Initialize(IReloadedHooks hooks)
    {
        SigScan(PresentRenderTargetSignature, " ", address =>
        {
            try
            {
                if (_presentHook != null) return;
                _presentHook = hooks.CreateHook<PresentRenderTargetDelegate>(
                    PresentRenderTargetHook, address).Activate();
                LogDebugOnly($"[RemoveLetterbox] Presentation hooked at 0x{address:X}.");
            }
            catch (Exception ex)
            {
                LogErrorDebugOnly($"[RemoveLetterbox] Failed to hook presentation: {ex.Message}");
            }
        });

        SigScan(ProjectionAspectReferenceSignature, " ", address =>
        {
            try
            {
                nint aspectAddress = (nint)GetGlobalAddress(address + 4);
                lock (AspectLock)
                {
                    _projectionAspectAddress = aspectAddress;
                    WriteProjectionAspectLocked(_requestedAspect);
                }
                LogDebugOnly($"[RemoveLetterbox] Projection aspect hooked at 0x{aspectAddress:X}.");
            }
            catch (Exception ex)
            {
                LogErrorDebugOnly($"[RemoveLetterbox] Failed to hook projection aspect: {ex.Message}");
            }
        });

        SigScan(UploadShaderConstantSignature, " ", address =>
        {
            try
            {
                if (_uploadShaderConstantHook != null) return;
                _uploadShaderConstantHook = hooks.CreateHook<UploadShaderConstantDelegate>(
                    UploadShaderConstantHook, address).Activate();
                LogDebugOnly($"[RemoveLetterbox] UI constant hooked at 0x{address:X}.");
            }
            catch (Exception ex)
            {
                LogErrorDebugOnly($"[RemoveLetterbox] Failed to hook UI constant: {ex.Message}");
            }
        });

        SigScan(LoadVertexShaderSignature, " ", address =>
        {
            try
            {
                if (_loadVertexShaderHook != null) return;
                _loadVertexShaderHook = hooks.CreateHook<LoadVertexShaderDelegate>(
                    LoadVertexShaderHook, address).Activate();
                LogDebugOnly($"[RemoveLetterbox] Sprite shader hooked at 0x{address:X}.");
            }
            catch (Exception ex)
            {
                LogErrorDebugOnly($"[RemoveLetterbox] Failed to hook sprite shader: {ex.Message}");
            }
        });

        SigScan(RenderStuffLowSignature, " ", address =>
        {
            try
            {
                if (_renderStuffLowHook != null) return;
                _renderStuffLowHook = hooks.CreateHook<RenderStuffLowDelegate>(
                    RenderStuffLowHook, address).Activate();
                LogDebugOnly(
                    $"[RemoveLetterbox] Sprite render classification hooked at 0x{address:X}.");
            }
            catch (Exception ex)
            {
                LogErrorDebugOnly(
                    $"[RemoveLetterbox] Failed to hook sprite render: {ex.Message}");
            }
        });

        SigScan(BattleOverlaySignature, " ", address =>
        {
            try
            {
                if (_battleOverlayHook != null) return;
                _battleOverlayHook = hooks.CreateHook<BattleOverlayDelegate>(
                    BattleOverlayHook, address).Activate();
                LogDebugOnly(
                    $"[RemoveLetterbox] Battle overlay classification hooked at 0x{address:X}.");
            }
            catch (Exception ex)
            {
                LogErrorDebugOnly(
                    $"[RemoveLetterbox] Failed to hook battle overlay classification: {ex.Message}");
            }
        });

        SigScan(BattleTargetRendererSignature, " ", address =>
        {
            try
            {
                if (_battleTargetRendererHook != null) return;
                _battleTargetRendererHook = hooks.CreateHook<BattleTargetRendererDelegate>(
                    BattleTargetRendererHook, address).Activate();
                LogDebugOnly(
                    $"[RemoveLetterbox] Battle target-marker classification hooked at 0x{address:X}.");
            }
            catch (Exception ex)
            {
                LogErrorDebugOnly(
                    $"[RemoveLetterbox] Failed to hook battle target-marker classification: {ex.Message}");
            }
        });

        SigScan(WorldToLogicalScreenCallSignature, " ", address =>
        {
            try
            {
                if (_worldToLogicalScreenHook != null) return;
                nint callAddress = address + WorldToLogicalScreenCallOffset;
                nint functionAddress = (nint)GetGlobalAddress(callAddress + 1);
                _worldToLogicalScreenHook = hooks.CreateHook<WorldToLogicalScreenDelegate>(
                    WorldToLogicalScreenHook, functionAddress).Activate();
                LogDebugOnly(
                    $"[RemoveLetterbox] Battle projection correction hooked at " +
                    $"0x{functionAddress:X}.");
            }
            catch (Exception ex)
            {
                LogErrorDebugOnly(
                    $"[RemoveLetterbox] Failed to hook battle projection correction: {ex.Message}");
            }
        });

        SigScan(TvListingMainSignature, " ", address =>
        {
            try
            {
                if (_tvListingMainHook != null) return;
                _tvListingMainHook = hooks.CreateHook<TvListingMainDelegate>(
                    TvListingMainHook, address).Activate();
                LogDebugOnly(
                    $"[RemoveLetterbox] TV Listings frame classification hooked at 0x{address:X}.");
            }
            catch (Exception ex)
            {
                LogErrorDebugOnly(
                    $"[RemoveLetterbox] Failed to hook TV Listings frame classification: {ex.Message}");
            }
        });

        SigScan(DrawSolidRectangleSignature, " ", address =>
        {
            try
            {
                if (_drawSolidRectangleHook != null) return;
                _drawSolidRectangleHook = hooks.CreateHook<DrawSolidRectangleDelegate>(
                    DrawSolidRectangleHook, address).Activate();
                LogDebugOnly(
                    $"[RemoveLetterbox] Rect hooked at 0x{address:X}.");
            }
            catch (Exception ex)
            {
                LogErrorDebugOnly(
                    $"[RemoveLetterbox] Failed to hook rect: {ex.Message}");
            }
        });
    }

    private static void TvListingMainHook(
        nint animation,
        ushort channel,
        nint channelEntries)
    {
        bool previous = _insideTvListingMain;
        _insideTvListingMain = true;
        try
        {
            _tvListingMainHook!.OriginalFunction(animation, channel, channelEntries);
        }
        finally
        {
            _insideTvListingMain = previous;
        }
    }

    private static void DrawSolidRectangleHook(
        nint color,
        nint rectangle,
        float depth,
        int resetRenderState)
    {
        float outputAspect = _outputAspect;
        if (!_insideTvListingMain || color == 0 || rectangle == 0 ||
            *(uint*)color != 0xff000000U ||
            !float.IsFinite(outputAspect) || outputAspect <= 0.0f ||
            MathF.Abs(outputAspect - StockAspect) <= 0.0001f)
        {
            _drawSolidRectangleHook!.OriginalFunction(
                color, rectangle, depth, resetRenderState);
            return;
        }

        float* source = (float*)rectangle;
        float x = source[0];
        float y = source[1];
        float width = source[2];
        float height = source[3];
        if (!float.IsFinite(x) || !float.IsFinite(y) ||
            !float.IsFinite(width) || !float.IsFinite(height))
        {
            _drawSolidRectangleHook!.OriginalFunction(
                color, rectangle, depth, resetRenderState);
            return;
        }

        float scaleX = outputAspect > StockAspect
            ? StockAspect / outputAspect : 1.0f;
        float scaleY = outputAspect < StockAspect
            ? outputAspect / StockAspect : 1.0f;
        float* corrected = stackalloc float[4];
        corrected[2] = width * scaleX;
        corrected[3] = height * scaleY;
        corrected[0] = x + (width - corrected[2]) * 0.5f;
        corrected[1] = y + (height - corrected[3]) * 0.5f;

        _drawSolidRectangleHook!.OriginalFunction(
            color, (nint)corrected, depth, resetRenderState);

        if (!_loggedTvListingCorrection)
        {
            _loggedTvListingCorrection = true;
            LogDebugOnly(
                $"[RemoveLetterbox] TV Listings proportional frame " +
                $"(rect {width:0.###}x{height:0.###}, " +
                $"scale {scaleX:0.####},{scaleY:0.####})");
        }
    }

    private static void BattleOverlayHook(nint parameter1, nint battlePanel)
    {
        bool previous = _insideBattleOverlay;
        _insideBattleOverlay = true;
        try
        {
            _battleOverlayHook!.OriginalFunction(parameter1, battlePanel);
        }
        finally
        {
            _insideBattleOverlay = previous;
        }
    }

    private static void BattleTargetRendererHook(
        nint parameter1,
        nint markerState,
        nint enemy)
    {
        bool previous = _insideBattleTargetRenderer;
        _insideBattleTargetRenderer = true;
        try
        {
            _battleTargetRendererHook!.OriginalFunction(parameter1, markerState, enemy);
        }
        finally
        {
            _insideBattleTargetRenderer = previous;
        }
    }

    private static int WorldToLogicalScreenHook(nint worldPosition, nint screenPosition)
    {
        int result = _worldToLogicalScreenHook!.OriginalFunction(
            worldPosition, screenPosition);
        if (result == 0 ||
            (!_insideBattleOverlay && !_insideBattleTargetRenderer) ||
            screenPosition == 0)
            return result;

        float outputAspect = _outputAspect;
        if (!float.IsFinite(outputAspect) || outputAspect <= 0.0f ||
            MathF.Abs(outputAspect - StockAspect) <= 0.0001f)
        {
            return result;
        }

        float* screen = (float*)screenPosition;
        if (outputAspect > StockAspect)
        {
            screen[0] = 960.0f + (screen[0] - 960.0f) * outputAspect / StockAspect;
        }
        else
        {
            screen[1] = 540.0f + (screen[1] - 540.0f) * StockAspect / outputAspect;
        }

        if (!_loggedBattleProjectionCorrection)
        {
            _loggedBattleProjectionCorrection = true;
            LogDebugOnly(
                $"[RemoveLetterbox] Battle projected-marker alignment " +
                $"(output aspect {outputAspect:0.####})");
        }
        return result;
    }

    private static void PresentRenderTargetHook(
        nint renderTarget,
        float left,
        float top,
        float width,
        float height,
        float outputWidth,
        float outputHeight)
    {
        if (!IsCenteredOutputFit(left, top, width, height, outputWidth, outputHeight))
        {
            _presentHook!.OriginalFunction(
                renderTarget, left, top, width, height, outputWidth, outputHeight);
            return;
        }

        float outputAspect = outputWidth / outputHeight;
        UpdateProjectionAspect(outputAspect);
        _outputAspect = outputAspect;
        _insideFinalComposite = true;
        try
        {
            _presentHook!.OriginalFunction(
                renderTarget,
                0.0f,
                0.0f,
                outputWidth,
                outputHeight,
                outputWidth,
                outputHeight);
        }
        finally
        {
            _insideFinalComposite = false;
        }

        if (!_loggedActive)
        {
            _loggedActive = true;
            LogDebugOnly(
                $"[RemoveLetterbox] Full output ({outputWidth:0}x{outputHeight:0}, " +
                $"aspect {outputAspect:0.####})");
        }
    }

    private static nuint UploadShaderConstantHook(
        nint shaderConstant,
        nint parameter2,
        nint parameter3,
        nint sourceData)
    {
        if (sourceData == 0 || shaderConstant == 0)
        {
            return _uploadShaderConstantHook!.OriginalFunction(
                shaderConstant, parameter2, parameter3, sourceData);
        }

        bool isUiScreen;
        lock (UiConstantLock)
        {
            isUiScreen = UiScreenConstants.Contains(shaderConstant);
            if (!isUiScreen && *(int*)(shaderConstant + 0x64) == sizeof(float) * 2 &&
                HasAsciiName(shaderConstant + 0x20, "uScreen"))
            {
                nint owner = *(nint*)(shaderConstant + 0x78);
                if (owner != 0 && HasAsciiName(owner + 0x90350, SpriteVertexShaderPath))
                {
                    UiScreenConstants.Add(shaderConstant);
                    isUiScreen = true;
                    LogDebugOnly(
                        $"[RemoveLetterbox] ui uScreen constant 0x{shaderConstant:X} " +
                        $"({((float*)sourceData)[0]:0.###}x{((float*)sourceData)[1]:0.###})");
                }
            }
        }

        if (!isUiScreen || !_spriteShaderPatched)
        {
            if (TryUploadCorrectedEffectMatrix(
                    shaderConstant, parameter2, parameter3, sourceData, out nuint effectResult))
            {
                return effectResult;
            }
            if (TryUploadCorrectedPrimitive2dMatrix(
                    shaderConstant, parameter2, parameter3, sourceData, out nuint correctedResult))
            {
                return correctedResult;
            }
            return _uploadShaderConstantHook!.OriginalFunction(
                shaderConstant, parameter2, parameter3, sourceData);
        }

        float* screen = (float*)sourceData;
        nuint result = _uploadShaderConstantHook!.OriginalFunction(
            shaderConstant, parameter2, parameter3, sourceData);
        _uiScreenConstant = shaderConstant;
        _uiOriginalWidth = screen[0];
        _uiOriginalHeight = screen[1];
        PrepareSpriteCanvas(_currentSpriteProportional);
        return result;
    }

    private static nint RenderStuffLowHook(
        int primitiveType,
        uint shaderFlags,
        nint parameter3,
        int parameter4,
        int parameter5,
        nint parameter6,
        nint vertices)
    {
        float minX = 0.0f;
        float minY = 0.0f;
        float maxX = 0.0f;
        float maxY = 0.0f;
        bool isMovie = (shaderFlags & MovieShaderFlag) != 0;
        bool isSprite = !isMovie && (shaderFlags & SpriteShaderFlag) != 0;
        bool hasBounds = (isSprite || isMovie) && TryGetSpriteBounds(
            vertices, out minX, out minY, out maxX, out maxY);
        bool fullScreenQuad = isSprite && hasBounds &&
            IsFullScreenQuad(minX, minY, maxX, maxY);
        bool fullScreenMovie = isMovie &&
            ((hasBounds && IsFullScreenQuad(minX, minY, maxX, maxY)) ||
             (!hasBounds && primitiveType == 0x0C000000 &&
              parameter4 == SpriteVertexCount && parameter5 == SpriteVertexCount));
        nint submittedVertices = vertices;
        byte* correctedMovieVertices = null;
        float outputAspect = _outputAspect;
        if (fullScreenMovie && hasBounds &&
            float.IsFinite(outputAspect) && outputAspect > 0.0f &&
            MathF.Abs(outputAspect - StockAspect) > 0.0001f)
        {
            const int movieVertexBytes = SpriteVertexStride * SpriteVertexCount;
            byte* movieVertexBuffer = stackalloc byte[movieVertexBytes];
            correctedMovieVertices = movieVertexBuffer;
            Buffer.MemoryCopy(
                (void*)vertices, correctedMovieVertices,
                movieVertexBytes, movieVertexBytes);

            float centerX = (minX + maxX) * 0.5f;
            float centerY = (minY + maxY) * 0.5f;
            float scaleX = outputAspect > StockAspect
                ? StockAspect / outputAspect : 1.0f;
            float scaleY = outputAspect < StockAspect
                ? outputAspect / StockAspect : 1.0f;
            for (int i = 0; i < SpriteVertexCount; i++)
            {
                byte* vertex = correctedMovieVertices + i * SpriteVertexStride;
                float* x = (float*)(vertex + 12);
                float* y = (float*)(vertex + 16);
                *x = centerX + (*x - centerX) * scaleX;
                *y = centerY + (*y - centerY) * scaleY;
            }
            submittedVertices = (nint)correctedMovieVertices;

            if (!_loggedMovieAspectFit)
            {
                _loggedMovieAspectFit = true;
                LogDebugOnly(
                    $"[RemoveLetterbox] mobie aspect " +
                    $"(scale {scaleX:0.####},{scaleY:0.####})");
            }
        }

        if (isSprite && hasBounds)
            _lastSpriteProportional = !fullScreenQuad;
        bool logicalUiCanvas = IsLogicalUiCanvas();
        bool spriteQuadWithoutPointer = !hasBounds &&
            primitiveType == 0x0C000000 &&
            parameter4 == SpriteVertexCount &&
            parameter5 == SpriteVertexCount;
        bool standardProportionalUi = isSprite &&
            (hasBounds
                ? !fullScreenQuad
                : spriteQuadWithoutPointer || logicalUiCanvas || _lastSpriteProportional);
        bool proportionalUi = !_insideFinalComposite && standardProportionalUi;

        if (fullScreenQuad && !_loggedFullScreenQuad)
        {
            _loggedFullScreenQuad = true;
            LogDebugOnly(
                $"[RemoveLetterbox] fullscreen sprite " +
                $"({minX:0.###},{minY:0.###})-({maxX:0.###},{maxY:0.###})");
        }

        bool previous = _currentSpriteProportional;
        _currentSpriteProportional = proportionalUi;
        PrepareSpriteCanvas(proportionalUi);
        try
        {
            return _renderStuffLowHook!.OriginalFunction(
                primitiveType,
                shaderFlags,
                parameter3,
                parameter4,
                parameter5,
                parameter6,
                submittedVertices);
        }
        finally
        {
            _currentSpriteProportional = previous;
        }
    }

    private static void PrepareSpriteCanvas(bool proportionalUi)
    {
        nint shaderConstant = _uiScreenConstant;
        float originalWidth = _uiOriginalWidth;
        if (!_spriteShaderPatched || shaderConstant == 0 || originalWidth <= 0.0f)
            return;

        float outputAspect = _outputAspect;
        if (!float.IsFinite(outputAspect) || outputAspect <= 0.0f)
            outputAspect = StockAspect;

        float width = originalWidth;
        float height = _uiOriginalHeight;
        if (proportionalUi)
        {
            if (outputAspect >= StockAspect)
                width *= outputAspect / StockAspect;
            else
                height *= StockAspect / outputAspect;
        }
        WriteSpriteCanvas(
            shaderConstant,
            width,
            height,
            originalWidth * 0.5f,
            _uiOriginalHeight * 0.5f);

        if (proportionalUi &&
            MathF.Abs(outputAspect - StockAspect) > 0.0001f &&
            !_loggedUiCorrection)
        {
            _loggedUiCorrection = true;
            LogDebugOnly(
                $"[RemoveLetterbox] safe aspect ui " +
                $"({originalWidth:0.###}x{_uiOriginalHeight:0.###} -> " +
                $"{width:0.###}x{height:0.###}).");
        }
    }

    private static bool TryGetSpriteBounds(
        nint vertices,
        out float minX,
        out float minY,
        out float maxX,
        out float maxY)
    {
        minX = minY = float.PositiveInfinity;
        maxX = maxY = float.NegativeInfinity;
        const int bytesRequired = SpriteVertexStride * SpriteVertexCount;
        if (!IsReadableRange(vertices, bytesRequired)) return false;

        byte* vertex = (byte*)vertices;
        for (int i = 0; i < SpriteVertexCount; i++, vertex += SpriteVertexStride)
        {
            float x = *(float*)(vertex + 12);
            float y = *(float*)(vertex + 16);
            if (!float.IsFinite(x) || !float.IsFinite(y) ||
                MathF.Abs(x) > 100000.0f || MathF.Abs(y) > 100000.0f)
            {
                return false;
            }

            minX = MathF.Min(minX, x);
            minY = MathF.Min(minY, y);
            maxX = MathF.Max(maxX, x);
            maxY = MathF.Max(maxY, y);
        }
        return maxX > minX && maxY > minY;
    }

    private static bool IsFullScreenQuad(float minX, float minY, float maxX, float maxY)
    {
        float width = maxX - minX;
        float height = maxY - minY;

        float screenWidth = _uiOriginalWidth;
        float screenHeight = _uiOriginalHeight;
        if (screenWidth > 0.0f && screenHeight > 0.0f)
        {
            bool coversWidth = width >= screenWidth * 0.95f;
            bool coversHeight = height >= screenHeight * 0.95f;
            if ((coversWidth && coversHeight) ||
                (coversWidth && height <= 4.0f) ||
                (coversHeight && width <= 4.0f))
            {
                return true;
            }
        }

        // the game logic or the canvas itself i think, still runs at 960x540
        // some other functions shows that it also runs at 960x544 so this prolly
        // wont fix everythin
        const float logicalWidth = 960.0f;
        const float logicalHeight = 540.0f;
        const float logicalTolerance = 2.0f;
        return MathF.Abs(minX) <= logicalTolerance &&
               MathF.Abs(minY) <= logicalTolerance &&
               MathF.Abs(width - logicalWidth) <= logicalTolerance &&
               MathF.Abs(height - logicalHeight) <= logicalTolerance;
    }

    private static bool IsLogicalUiCanvas()
    {
        float width = _uiOriginalWidth;
        float height = _uiOriginalHeight;
        return width >= 480.0f && height >= 270.0f &&
               MathF.Abs(width / height - StockAspect) <= 0.01f;
    }

    private static bool IsReadableRange(nint address, int length)
    {
        if (address < 0x10000 || length <= 0) return false;
        if (VirtualQuery(
                address,
                out MemoryBasicInformation information,
                (nuint)sizeof(MemoryBasicInformation)) == 0 ||
            information.State != MemCommit ||
            (information.Protect & (PageGuard | PageNoAccess)) != 0)
        {
            return false;
        }

        nuint offset = (nuint)(address - information.BaseAddress);
        return offset <= information.RegionSize &&
               (nuint)length <= information.RegionSize - offset;
    }

    private static nint LoadVertexShaderHook(
        nint shaderPath,
        nint parameter2,
        nint parameter3,
        nint parameter4)
    {
        nint shader = _loadVertexShaderHook!.OriginalFunction(
            shaderPath, parameter2, parameter3, parameter4);
        if (shader != 0 && shaderPath != 0 &&
            HasAsciiName(shaderPath, SpriteVertexShaderPath))
        {
            TryPatchSpriteShader(shader);
        }
        return shader;
    }

    private static void TryPatchSpriteShader(nint shader)
    {
        try
        {
            byte* bytecode = *(byte**)(shader + 0x10);
            nuint bytecodeSize = *(nuint*)(shader + 0x18);
            if (bytecode == null || bytecodeSize < 64 || bytecodeSize > int.MaxValue)
            {
                LogErrorDebugOnly("[RemoveLetterbox] Sprite shader bytecode not found.");
                return;
            }

            ReadOnlySpan<byte> instruction = SpriteXOriginInstruction;
            Span<byte> blob = new(bytecode, (int)bytecodeSize);
            int instructionOffset = blob.IndexOf(instruction);
            if (instructionOffset < 0)
            {
                LogErrorDebugOnly("[RemoveLetterbox] Sprite shader x-origin instruction not found.");
                return;
            }

            // cb0[2].x is the shader's unused test slot
            // The uScreen stores half of the uncorrected logical width there,
            // allowing uScreen.x to control scale without changing the X origin
            // mad r0.x, -cb0[2].x, 1.0, r0.y
            BitConverter.TryWriteBytes(blob[(instructionOffset + 24)..], 2U);
            BitConverter.TryWriteBytes(blob[(instructionOffset + 32)..], 1.0f);

            ReadOnlySpan<byte> yInstruction = SpriteYOriginInstruction;
            int yInstructionOffset = blob.IndexOf(yInstruction);
            if (yInstructionOffset < 0)
            {
                LogErrorDebugOnly("[RemoveLetterbox] Sprite shader Y-origin instruction was not found.");
                return;
            }
            BitConverter.TryWriteBytes(blob[(yInstructionOffset + 20)..], 2U);
            BitConverter.TryWriteBytes(blob[(yInstructionOffset + 28)..], 1.0f);
            WriteDxbcChecksum(blob);
            _spriteShaderPatched = true;
            LogDebugOnly(
                "[RemoveLetterbox] Sprite shader center patch applied.");
        }
        catch (Exception ex)
        {
            LogErrorDebugOnly($"[RemoveLetterbox] Failed to patch sprite shader: {ex.Message}");
        }
    }

    private static bool HasAsciiName(nint address, string expected)
    {
        if (address == 0) return false;
        byte* text = (byte*)address;
        for (int i = 0; i < expected.Length; i++)
        {
            if (text[i] != (byte)expected[i]) return false;
        }
        return text[expected.Length] == 0;
    }

    private static void WriteSpriteCanvas(
        nint shaderConstant,
        float width,
        float height,
        float centerX,
        float centerY)
    {
        nint owner = *(nint*)(shaderConstant + 0x78);
        int bufferIndex = *(int*)(shaderConstant + 0x1C);
        if (owner == 0 || bufferIndex < 0) return;

        nint buffer = *(nint*)(owner + 0x28 + bufferIndex * sizeof(nint));
        if (buffer == 0) return;

        if (float.IsFinite(width))
        {
            int screenOffset = *(int*)(shaderConstant + 0x68);
            *(float*)(buffer + screenOffset) = width;
            *(float*)(buffer + screenOffset + sizeof(float)) = height;
        }

        *(float*)(buffer + 32) = centerX;
        *(float*)(buffer + 36) = centerY;
        *(int*)(owner + 0xA8 + bufferIndex * sizeof(int)) = 1;
    }

    private static bool TryUploadCorrectedPrimitive2dMatrix(
        nint shaderConstant,
        nint parameter2,
        nint parameter3,
        nint sourceData,
        out nuint result)
    {
        result = 0;
        if (_insideFinalComposite ||
            *(int*)(shaderConstant + 0x64) != sizeof(float) * 16 ||
            !HasAsciiName(shaderConstant + 0x20, "Mat2dCamera"))
        {
            return false;
        }

        nint owner = *(nint*)(shaderConstant + 0x78);
        if (owner == 0 ||
            (!HasAsciiName(owner + 0x90350, Primitive2dVertexShaderPath) &&
             !HasAsciiName(owner + 0x90350, Primitive2dFontVertexShaderPath)))
        {
            return false;
        }

        float aspect = _outputAspect;
        if (!float.IsFinite(aspect) || aspect <= 0.0f)
            aspect = StockAspect;
        float scaleX = MathF.Min(1.0f, StockAspect / aspect);
        float scaleY = MathF.Min(1.0f, aspect / StockAspect);
        if (MathF.Abs(scaleX - 1.0f) <= 0.0001f &&
            MathF.Abs(scaleY - 1.0f) <= 0.0001f)
        {
            return false;
        }

        float* source = (float*)sourceData;
        float* corrected = stackalloc float[16];
        for (int i = 0; i < 16; i++) corrected[i] = source[i];
        for (int row = 0; row < 4; row++)
        {
            corrected[row * 4] *= scaleX;
            corrected[row * 4 + 1] *= scaleY;
        }

        result = _uploadShaderConstantHook!.OriginalFunction(
            shaderConstant, parameter2, parameter3, (nint)corrected);
        if (!_loggedPrimitive2dCorrection)
        {
            _loggedPrimitive2dCorrection = true;
            LogDebugOnly(
                $"[RemoveLetterbox] 2D safe area matrix " +
                $"(scale {scaleX:0.####},{scaleY:0.####}).");
        }
        return true;
    }

    private static bool TryUploadCorrectedEffectMatrix(
        nint shaderConstant,
        nint parameter2,
        nint parameter3,
        nint sourceData,
        out nuint result)
    {
        result = 0;
        if (_insideFinalComposite ||
            *(int*)(shaderConstant + 0x64) != sizeof(float) * 16 ||
            !HasAsciiName(shaderConstant + 0x20, "uWorldViewProj"))
        {
            return false;
        }

        nint owner = *(nint*)(shaderConstant + 0x78);
        if (owner == 0 || !HasAsciiName(owner + 0x90350, EffectVertexShaderPath))
            return false;

        float outputAspect = _outputAspect;
        if (!float.IsFinite(outputAspect) || outputAspect <= 0.0f ||
            MathF.Abs(outputAspect - StockAspect) <= 0.0001f)
        {
            return false;
        }

        float* source = (float*)sourceData;
        float matrixAspect = GetMatrixProjectionAspect(source);
        if (!float.IsFinite(matrixAspect) ||
            MathF.Abs(matrixAspect - StockAspect) > MatrixAspectTolerance)
        {
            return false;
        }

        // the compositor stretches its 16x9 render target to the
        // output once letterboxing is removed
        // pre-scaling their clip-space axis cancels that stretching
        float scaleX = MathF.Min(1.0f, StockAspect / outputAspect);
        float scaleY = MathF.Min(1.0f, outputAspect / StockAspect);
        float* corrected = stackalloc float[16];
        for (int i = 0; i < 16; i++) corrected[i] = source[i];
        for (int row = 0; row < 4; row++)
        {
            corrected[row * 4] *= scaleX;
            corrected[row * 4 + 1] *= scaleY;
        }

        result = _uploadShaderConstantHook!.OriginalFunction(
            shaderConstant, parameter2, parameter3, (nint)corrected);
        if (!_loggedEffectMatrixCorrection)
        {
            _loggedEffectMatrixCorrection = true;
            LogDebugOnly(
                $"[RemoveLetterbox] effect matrix " +
                $"(matrix {matrixAspect:0.####}, scale {scaleX:0.####},{scaleY:0.####}).");
        }
        return true;
    }

    private static float GetMatrixProjectionAspect(float* matrix)
    {
        float clipX = MathF.Sqrt(
            matrix[0] * matrix[0] +
            matrix[4] * matrix[4] +
            matrix[8] * matrix[8]);
        float clipY = MathF.Sqrt(
            matrix[1] * matrix[1] +
            matrix[5] * matrix[5] +
            matrix[9] * matrix[9]);
        return clipX > 0.000001f ? clipY / clipX : float.NaN;
    }

    private static void WriteDxbcChecksum(Span<byte> blob)
    {
        if (blob.Length <= 20 ||
            blob[0] != (byte)'D' || blob[1] != (byte)'X' ||
            blob[2] != (byte)'B' || blob[3] != (byte)'C')
        {
            throw new InvalidDataException("Invalid dxbc container.");
        }

        uint[] state = [0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476];
        ReadOnlySpan<byte> data = blob[20..];
        uint bitCount = unchecked((uint)data.Length * 8);
        int completeLength = data.Length & ~63;
        for (int offset = 0; offset < completeLength; offset += 64)
            TransformMd5Block(state, data.Slice(offset, 64));

        int remaining = data.Length - completeLength;
        Span<byte> finalBlock = stackalloc byte[64];
        finalBlock.Clear();
        if (remaining >= 56)
        {
            data[completeLength..].CopyTo(finalBlock);
            finalBlock[remaining] = 0x80;
            TransformMd5Block(state, finalBlock);
            finalBlock.Clear();
            BitConverter.TryWriteBytes(finalBlock, bitCount);
        }
        else
        {
            BitConverter.TryWriteBytes(finalBlock, bitCount);
            data[completeLength..].CopyTo(finalBlock[4..]);
            finalBlock[4 + remaining] = 0x80;
        }

        BitConverter.TryWriteBytes(finalBlock[60..], (bitCount >> 2) | 1U);
        TransformMd5Block(state, finalBlock);
        for (int i = 0; i < state.Length; i++)
            BitConverter.TryWriteBytes(blob.Slice(4 + i * 4, 4), state[i]);
    }

    private static void TransformMd5Block(uint[] state, ReadOnlySpan<byte> block)
    {
        ReadOnlySpan<int> shifts =
        [
            7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
            5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
            4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
            6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21
        ];
        ReadOnlySpan<uint> constants =
        [
            0xD76AA478, 0xE8C7B756, 0x242070DB, 0xC1BDCEEE,
            0xF57C0FAF, 0x4787C62A, 0xA8304613, 0xFD469501,
            0x698098D8, 0x8B44F7AF, 0xFFFF5BB1, 0x895CD7BE,
            0x6B901122, 0xFD987193, 0xA679438E, 0x49B40821,
            0xF61E2562, 0xC040B340, 0x265E5A51, 0xE9B6C7AA,
            0xD62F105D, 0x02441453, 0xD8A1E681, 0xE7D3FBC8,
            0x21E1CDE6, 0xC33707D6, 0xF4D50D87, 0x455A14ED,
            0xA9E3E905, 0xFCEFA3F8, 0x676F02D9, 0x8D2A4C8A,
            0xFFFA3942, 0x8771F681, 0x6D9D6122, 0xFDE5380C,
            0xA4BEEA44, 0x4BDECFA9, 0xF6BB4B60, 0xBEBFBC70,
            0x289B7EC6, 0xEAA127FA, 0xD4EF3085, 0x04881D05,
            0xD9D4D039, 0xE6DB99E5, 0x1FA27CF8, 0xC4AC5665,
            0xF4292244, 0x432AFF97, 0xAB9423A7, 0xFC93A039,
            0x655B59C3, 0x8F0CCC92, 0xFFEFF47D, 0x85845DD1,
            0x6FA87E4F, 0xFE2CE6E0, 0xA3014314, 0x4E0811A1,
            0xF7537E82, 0xBD3AF235, 0x2AD7D2BB, 0xEB86D391
        ];

        Span<uint> words = stackalloc uint[16];
        for (int i = 0; i < words.Length; i++)
            words[i] = BitConverter.ToUInt32(block.Slice(i * 4, 4));

        uint a = state[0];
        uint b = state[1];
        uint c = state[2];
        uint d = state[3];
        unchecked
        {
            for (int i = 0; i < 64; i++)
            {
                uint f;
                int word;
                if (i < 16)
                {
                    f = (b & c) | (~b & d);
                    word = i;
                }
                else if (i < 32)
                {
                    f = (d & b) | (~d & c);
                    word = (5 * i + 1) & 15;
                }
                else if (i < 48)
                {
                    f = b ^ c ^ d;
                    word = (3 * i + 5) & 15;
                }
                else
                {
                    f = c ^ (b | ~d);
                    word = (7 * i) & 15;
                }

                uint previousD = d;
                d = c;
                c = b;
                b += BitOperations.RotateLeft(
                    a + f + constants[i] + words[word], shifts[i]);
                a = previousD;
            }

            state[0] += a;
            state[1] += b;
            state[2] += c;
            state[3] += d;
        }
    }

    private static bool IsCenteredOutputFit(
        float left,
        float top,
        float width,
        float height,
        float outputWidth,
        float outputHeight)
    {
        if (!float.IsFinite(left) || !float.IsFinite(top) ||
            !float.IsFinite(width) || !float.IsFinite(height) ||
            !float.IsFinite(outputWidth) || !float.IsFinite(outputHeight) ||
            width <= 0.0f || height <= 0.0f ||
            outputWidth <= 0.0f || outputHeight <= 0.0f ||
            width > outputWidth + DimensionTolerance ||
            height > outputHeight + DimensionTolerance)
        {
            return false;
        }

        float expectedLeft = (outputWidth - width) * 0.5f;
        float expectedTop = (outputHeight - height) * 0.5f;
        return MathF.Abs(left - expectedLeft) <= DimensionTolerance &&
               MathF.Abs(top - expectedTop) <= DimensionTolerance;
    }

    private static void UpdateProjectionAspect(float aspect)
    {
        if (!float.IsFinite(aspect) || aspect <= 0.0f) return;

        lock (AspectLock)
        {
            _requestedAspect = aspect;
            if (_projectionAspectAddress != 0 &&
                (float.IsNaN(_writtenAspect) || MathF.Abs(_writtenAspect - aspect) > 0.0001f))
            {
                WriteProjectionAspectLocked(aspect);
            }
        }
    }

    private static void WriteProjectionAspectLocked(float aspect)
    {
        if (_projectionAspectAddress == 0) return;

        if (!VirtualProtect(
                _projectionAspectAddress,
                sizeof(float),
                PageExecuteReadWrite,
                out uint oldProtection))
        {
            LogWriteFailure();
            return;
        }

        try
        {
            *(float*)_projectionAspectAddress = aspect;
            _writtenAspect = aspect;
        }
        finally
        {
            if (!VirtualProtect(
                    _projectionAspectAddress,
                    sizeof(float),
                    oldProtection,
                    out _))
            {
                LogWriteFailure();
            }
        }
    }

    private static void LogWriteFailure()
    {
        if (_loggedWriteFailure) return;
        _loggedWriteFailure = true;
        LogErrorDebugOnly(
            $"[RemoveLetterbox] Could not update the projection aspect " +
            $"({Marshal.GetLastWin32Error()}).");
    }
}
