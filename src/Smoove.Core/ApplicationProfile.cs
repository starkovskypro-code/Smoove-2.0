namespace Smoove.Core;

public enum ApplicationScrollMode { Global, Custom, Excluded }

public sealed record ScrollSettings(double Distance = 1, double Acceleration = 60, double Smooth = 110,
    double Rise = 70, double Coast = 160, double AnimationTempo = 1,
    MotionEngineKind Engine = MotionEngineKind.Smoove, SmoothieProfile? Experimental = null)
{
    public MotionProfile MotionProfile => new(Rise * Smooth / 100000, Coast * Smooth / 100000);
    public void Validate()
    {
        if (!double.IsFinite(Distance + Acceleration + Smooth + Rise + Coast + AnimationTempo) ||
            Distance is < 0.5 or > 4 || Acceleration is < 0 or > 100 || Smooth is < 50 or > 200 ||
            Rise is < 5 or > 300 || Coast is < 5 or > 500 || AnimationTempo is < 0.5 or > 8 || !Enum.IsDefined(Engine))
            throw new ArgumentOutOfRangeException(nameof(ScrollSettings));
        MotionProfile.Validate();
        (Experimental ?? new()).Validate();
    }
}

public sealed record ApplicationProfile(string Path, ApplicationScrollMode Mode = ApplicationScrollMode.Global,
    ScrollSettings? Settings = null, bool Enabled = true, bool LegacyName = false)
{
    public ApplicationProfile Validated()
    {
        if (!Enum.IsDefined(Mode) || string.IsNullOrWhiteSpace(Path)) throw new ArgumentException("Некорректный профиль программы.");
        if (Mode == ApplicationScrollMode.Custom && Settings is null) throw new ArgumentException("Нет настроек своего профиля.");
        Settings?.Validate();
        return LegacyName ? this : this with { Path = ApplicationExclusion.Normalize(Path) };
    }
    public bool Matches(string executable) => new ApplicationExclusion(Path, Enabled, LegacyName).Matches(executable);
    public static ApplicationProfile[] Migrate(IEnumerable<ApplicationExclusion> exclusions) =>
        exclusions.Select(e => new ApplicationProfile(e.Path, ApplicationScrollMode.Excluded, Enabled: e.Enabled, LegacyName: e.LegacyName)).ToArray();
    public static ApplicationProfile? Find(IEnumerable<ApplicationProfile> profiles, string executable) =>
        profiles.FirstOrDefault(p => !p.LegacyName && p.Matches(executable)) ?? profiles.FirstOrDefault(p => p.Matches(executable));
    public static bool Allows(ApplicationProfile? profile, bool onlySelected) =>
        profile?.Mode != ApplicationScrollMode.Excluded && (!onlySelected || profile is not null);
}
