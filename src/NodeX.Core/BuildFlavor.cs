namespace NodeX.Core;

/// <summary>
/// Which edition of NodeX this binary is. The default (GitHub / sideload) edition has every feature. The
/// Google Play edition is built with <c>-p:PlayStore=true</c> (defines <c>PLAY_STORE</c>) and leaves out
/// transmitting from the car (Android Auto Talk button and steering-wheel talk), so the Play listing is a
/// listen-only car media app. Phone push-to-talk is not affected.
/// </summary>
public static class BuildFlavor
{
#if PLAY_STORE
    public const bool CarTalkAllowed = false;
#else
    public const bool CarTalkAllowed = true;
#endif
}
