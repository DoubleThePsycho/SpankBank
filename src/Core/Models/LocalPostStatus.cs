namespace SpankBank.Core.Models
{
    /// <summary>What this device holds for a post (device.sqlite Posts.Status).</summary>
    public enum LocalPostStatus
    {
        Unknown = 0,
        /// <summary>The original file from e621.</summary>
        Downloaded = 1,
        /// <summary>The original, recovered from the Wayback Machine after e621 deleted it (md5 verified).</summary>
        Recovered = 2,
        /// <summary>Only e621's downsized sample could be recovered; a stand-in for the original.</summary>
        SampleOnly = 3,
    }
}
