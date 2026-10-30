using COMS_MVC.Services;
using Xunit;

namespace COMS.Tests;

/// <summary>Smoke tests for the flood-news relevance classifier gates.</summary>
public sealed class FloodRelevanceTests
{
    [Fact]
    public void GenuineFloodEvent_IsRelevant()
    {
        Assert.True(FloodRelevance.IsRelevant(
            "Cebu flood hits riverside communities",
            "Major flooding displaced families in Cebu City. Rescue teams deployed."));
    }

    [Fact]
    public void UnrelatedTopic_IsNotRelevant()
    {
        Assert.False(FloodRelevance.IsRelevant(
            "New shopping mall opens downtown",
            "The mall features 200 stores and a cinema complex opening this weekend."));
    }

    [Fact]
    public void NegatedFloodMention_IsNotRelevant()
    {
        Assert.False(FloodRelevance.IsRelevant(
            "City council meeting minutes",
            "There was no flooding reported in any district this quarter."));
    }

    [Fact]
    public void EmptyTitleAndBody_IsNotRelevant()
    {
        Assert.False(FloodRelevance.IsRelevant("", ""));
        Assert.False(FloodRelevance.IsRelevant(null, null));
    }

    [Fact]
    public void CebuAreas_SortFirst()
    {
        Assert.True(FloodRelevance.IsCebuPriority(new[] { "Cebu City" }));
        Assert.False(FloodRelevance.IsCebuPriority(new[] { "Manila" }));
    }

    // ---- Bug 2 reproduction: political/budget stories must be rejected ----

    private const string BudgetTitle =
        "Watchdogs question transparency in House OK of 2027 budget bill";

    private const string BudgetBody =
        "Watchdog groups on Monday questioned the transparency of the House of " +
        "Representatives' approval of the proposed 2027 national budget, citing " +
        "concerns over flood control allocations and the lack of public itemization. " +
        "Lawmakers defended the process, saying all projects went through proper " +
        "deliberation in Manila.";

    [Fact]
    public void BudgetAccountabilityStory_WithoutFloodEvent_IsRejected()
    {
        Assert.False(FloodRelevance.IsRelevant(BudgetTitle, BudgetBody));
    }

    [Fact]
    public void PoliticalStory_MentioningManila_IsRejected()
    {
        Assert.False(FloodRelevance.IsRelevant(
            "Senate opens debate on proposed tax reform package",
            "Senators in Manila opened plenary debate on the tax reform package. " +
            "The bill hurdled committee level last week and faces floor amendments."));
    }

    [Fact]
    public void FloodControlBudget_WithoutEventOrPreventionWork_IsRejected()
    {
        Assert.False(FloodRelevance.IsRelevant(
            "House defends flood control fund use amid criticism",
            "Lawmakers defended the use of flood control funds in the 2027 budget, " +
            "saying every allocation followed congressional procedure. Watchdog " +
            "groups demanded a full accounting of the expenditures."));
    }

    [Fact]
    public void CrimeStory_WithIncidentalFloodMention_IsRejected()
    {
        Assert.False(FloodRelevance.IsRelevant(
            "Suspect nabbed in Quezon City robbery",
            "Police arrested a robbery suspect in Quezon City on Tuesday. Officers " +
            "said the getaway car was found on a flooded street near the river, " +
            "but the case is purely a criminal investigation."));
    }

    [Fact]
    public void WeatherForecast_WithoutFloodWarning_IsRejected()
    {
        Assert.False(FloodRelevance.IsRelevant(
            "Fair weather expected over Luzon this weekend",
            "PAGASA said fair weather will prevail over most of Luzon with isolated " +
            "rain showers in the afternoon. Temperatures will range from 25 to 33 degrees."));
    }

    [Fact]
    public void Typhoon_WithoutFloodConnection_IsRejected()
    {
        Assert.False(FloodRelevance.IsRelevant(
            "Typhoon maintains strength as it moves away",
            "The typhoon maintained its strength as it continued moving away from " +
            "the Philippine area of responsibility. No storm signals are in effect."));
    }

    // ---- Legitimate flood news must keep passing ----

    [Fact]
    public void OfficialFloodWarning_IsAccepted()
    {
        Assert.True(FloodRelevance.IsRelevant(
            "PAGASA issues flood warning for Cagayan Valley",
            "PAGASA issued a flood warning for rivers in Cagayan Valley on Friday. " +
            "Residents near the riverbanks were advised to evacuate to higher ground."));
    }

    [Fact]
    public void FlashFloodCebu_IsAccepted()
    {
        Assert.True(FloodRelevance.IsRelevant(
            "Flash floods hit Cebu City villages",
            "Flash floods hit several villages in Cebu City after hours of heavy rain. " +
            "Rescue teams evacuated stranded families as floodwaters rose waist-deep."));
    }

    [Fact]
    public void RiverOverflowEvacuation_IsAccepted()
    {
        Assert.True(FloodRelevance.IsRelevant(
            "River overflows, forces evacuations in Pampanga",
            "The river overflowed its banks in Pampanga, forcing the evacuation of " +
            "hundreds of families. Floodwaters submerged homes and rice fields."));
    }

    [Fact]
    public void Typhoon_WithExplicitFloodWarning_IsAccepted()
    {
        Assert.True(FloodRelevance.IsRelevant(
            "Typhoon to bring floods, storm surge to eastern coast",
            "The typhoon is expected to bring heavy flooding and storm surge to the " +
            "eastern coast. Authorities issued flood warnings and ordered evacuations."));
    }

    [Fact]
    public void FloodPreventionProject_IsAccepted()
    {
        Assert.True(FloodRelevance.IsRelevant(
            "DPWH completes flood control project in Cebu",
            "The DPWH completed a flood control project along the river in Cebu City. " +
            "The new drainage and embankment works aim to prevent urban flooding."));
    }

    [Fact]
    public void BodyOnlyWeatherWithEvacuation_IsAccepted()
    {
        Assert.True(FloodRelevance.IsRelevant(
            "Cebu villages brace for more rain",
            "Heavy rain and strong winds prompted the evacuation of residents in Cebu " +
            "as water levels continued to rise along the river."));
    }

    [Fact]
    public void PhilippineFocus_RejectsForeignFloodWithIncidentalPhilippinesMentions()
    {
        Assert.False(FloodRelevance.HasPhilippineFocus(
            "Floods hit China and Japan as Philippines and Philippine officials monitor " +
            "the situation in Beijing and Tokyo."));
    }

    [Fact]
    public void PhilippineFocus_AcceptsCountryOnlyStoryWithoutForeignLocations()
    {
        Assert.True(FloodRelevance.HasPhilippineFocus(
            "Flood warnings issued across the Philippines as monsoon rains intensify."));
    }
}
