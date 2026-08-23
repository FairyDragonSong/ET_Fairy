namespace ET.Server
{
    public static class AccountInputValidator
    {
        public static bool TryNormalizeLoginName(string value, out string normalized)
        {
            normalized = value?.Trim().ToLowerInvariant();
            if (normalized == null || normalized.Length < 3 || normalized.Length > 32)
            {
                return false;
            }

            foreach (char c in normalized)
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_')
                {
                    continue;
                }

                return false;
            }

            return true;
        }

        public static bool IsPasswordValid(string password)
        {
            return password != null && password.Length >= 8 && password.Length <= 128;
        }
    }
}
