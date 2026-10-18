namespace ET.Server
{
    /// <summary>
    /// 负责把账号统一为“去首尾空格的小写形式”，并限制账号只能包含 a-z、数字和下划线；密码目前只检查 8～128 个字符。
    /// </summary>
    public static class AccountInputValidator
    {
        // 规范化登录名，并拒绝长度或字符集不符合约束的输入。
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

        // 只校验密码长度边界，具体强度策略由上层产品规则决定。
        public static bool IsPasswordValid(string password)
        {
            return password != null && password.Length >= 8 && password.Length <= 128;
        }
    }
}
