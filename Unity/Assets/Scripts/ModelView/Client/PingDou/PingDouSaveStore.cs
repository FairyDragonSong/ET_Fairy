using System;
using System.Collections.Generic;
using UnityEngine;

namespace ET.Client
{
    [Serializable]
    public sealed class PingDouRecord
    {
        public string id;
        public int stars;
        public int score;
    }

    [Serializable]
    public sealed class PingDouProfile
    {
        public int version = 1;
        public string currentId;
        public bool muted;
        public List<PingDouRecord> records = new List<PingDouRecord>();
    }

    /// <summary>Versioned device-local demo saves. No claim of server authority or anti-cheat.</summary>
    public sealed class PingDouSaveStore
    {
        private readonly string prefix;
        public PingDouProfile Profile { get; private set; }

        public PingDouSaveStore(string prefix = "PingDou.v1.")
        {
            this.prefix = prefix;
            Profile = Read<PingDouProfile>("profile") ?? new PingDouProfile();
            if (Profile.version != 1 || Profile.records == null)
                Profile = new PingDouProfile();
        }

        private T Read<T>(string key)
            where T : class
        {
            try
            {
                string json = PlayerPrefs.GetString(prefix + key, "");
                return json.Length == 0 ? null : JsonUtility.FromJson<T>(json);
            }
            catch (Exception e)
            {
                Debug.LogWarning("PingDou ignored invalid local save: " + e.Message);
                return null;
            }
        }

        public PingDouRound Open(PingDouPattern pattern) => PingDouRound.Restore(pattern, Read<PingDouSnapshot>(pattern.id));
        public void Save(PingDouRound round)
        {
            Profile.currentId = round.Pattern.id;
            PlayerPrefs.SetString(prefix + round.Pattern.id, JsonUtility.ToJson(round.Capture()));
            SaveProfile();
        }

        public void SaveProfile()
        {
            PlayerPrefs.SetString(prefix + "profile", JsonUtility.ToJson(Profile));
            PlayerPrefs.Save();
        }

        public void Collect(PingDouRound round)
        {
            if (round.Phase != PingDouPhase.Complete)
                return;
            var record = Profile.records.Find(x => x.id == round.Pattern.id);
            if (record == null)
            {
                record = new PingDouRecord
                {
                    id = round.Pattern.id
                };
                Profile.records.Add(record);
            }

            record.stars = Math.Max(record.stars, round.Stars);
            record.score = Math.Max(record.score, round.Score);
            Save(round);
        }
    }
}
