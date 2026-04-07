using NzbWebDAV.Utils;

namespace NzbWebDAV.Config;

public static class MultiNodeMode
{
    public static bool IsEnabled =>
        !string.IsNullOrEmpty(EnvironmentUtil.GetDatabaseUrl());
}
