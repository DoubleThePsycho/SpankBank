namespace SpankBank.Core.Models;

public enum PostOrderField
{
    DateUploaded,
    DateDownloaded,
    Score,
    DateUpdated,
    FileSize,
    // TODO: Duration - no Duration column in Posts schema yet; add when schema has it
    // TODO: TagCount, GeneralTagCount, ArtistTagCount, ContributorTagCount,
    //       CopyrightTagCount, CharacterTagCount, SpeciesTagCount, InvalidTagCount,
    //       MetaTagCount - no pre-computed tag count columns in schema yet
}
