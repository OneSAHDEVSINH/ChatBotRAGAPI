using ChatBotRAGAPI.Services;

namespace ChatBotRAGAPI.Tests;

public class PorterStemmerTests
{
    // Examples from Porter's 1980 paper, one or more per step.
    [Theory]
    [InlineData("caresses", "caress")] [InlineData("ponies", "poni")] [InlineData("ties", "ti")]
    [InlineData("caress", "caress")] [InlineData("cats", "cat")]
    [InlineData("feed", "feed")] [InlineData("agreed", "agre")] /* the paper shows step 1b's "agree"; step 5a then drops the e */ [InlineData("plastered", "plaster")]
    [InlineData("bled", "bled")] [InlineData("motoring", "motor")] [InlineData("sing", "sing")]
    [InlineData("conflated", "conflat")] [InlineData("troubled", "troubl")] [InlineData("sized", "size")]
    [InlineData("hopping", "hop")] [InlineData("tanned", "tan")] [InlineData("falling", "fall")]
    [InlineData("hissing", "hiss")] [InlineData("fizzed", "fizz")] [InlineData("failing", "fail")] [InlineData("filing", "file")]
    [InlineData("happy", "happi")] [InlineData("sky", "sky")]
    [InlineData("relational", "relat")] [InlineData("conditional", "condit")] [InlineData("rational", "ration")]
    [InlineData("digitizer", "digit")] [InlineData("vietnamization", "vietnam")] [InlineData("predication", "predic")]
    [InlineData("operator", "oper")] [InlineData("feudalism", "feudal")] [InlineData("decisiveness", "decis")]
    [InlineData("hopefulness", "hope")] [InlineData("callousness", "callous")] [InlineData("formaliti", "formal")]
    [InlineData("sensitiviti", "sensit")] [InlineData("sensibiliti", "sensibl")]
    [InlineData("triplicate", "triplic")] [InlineData("formative", "form")] [InlineData("formalize", "formal")]
    [InlineData("electrical", "electr")] [InlineData("hopeful", "hope")] [InlineData("goodness", "good")]
    [InlineData("revival", "reviv")] [InlineData("allowance", "allow")] [InlineData("inference", "infer")]
    [InlineData("airliner", "airlin")] [InlineData("gyroscopic", "gyroscop")] [InlineData("adjustable", "adjust")]
    [InlineData("defensible", "defens")] [InlineData("irritant", "irrit")] [InlineData("replacement", "replac")]
    [InlineData("adjustment", "adjust")] [InlineData("dependent", "depend")] [InlineData("adoption", "adopt")]
    [InlineData("homologous", "homolog")] [InlineData("communism", "commun")] [InlineData("activate", "activ")]
    [InlineData("effective", "effect")] [InlineData("bowdlerize", "bowdler")]
    [InlineData("probate", "probat")] [InlineData("rate", "rate")] [InlineData("cease", "ceas")]
    [InlineData("controlling", "control")] [InlineData("roll", "roll")]
    public void Matches_the_reference_outputs(string word, string stem) => Assert.Equal(stem, PorterStemmer.Stem(word));

    [Theory]
    [InlineData("founded", "found")]
    [InlineData("costs", "cost")]
    [InlineData("pricing", "price")]
    [InlineData("prices", "price")]
    [InlineData("employees", "employee")]
    public void Word_forms_meet(string a, string b) => Assert.Equal(PorterStemmer.Stem(b), PorterStemmer.Stem(a));

    [Theory]
    [InlineData("q3")] [InlineData("x86")] [InlineData("2026")] [InlineData("is")] [InlineData("ai")]
    public void Codes_numbers_and_short_words_are_left_alone(string word) => Assert.Equal(word, PorterStemmer.Stem(word));
}

public class KeywordStemmingTests
{
    [Fact]
    public void A_question_finds_a_passage_using_another_form_of_the_word()
    {
        var index = new KeywordIndex();
        index.Upsert(Guid.NewGuid(), "Giriraj Digital was founded in 2016 in Ahmedabad.", "profile.txt");
        index.Upsert(Guid.NewGuid(), "The cafeteria menu changes weekly.", "menu.txt");

        var hit = Assert.Single(index.Search("When was it found", 5));
        Assert.Equal("profile.txt", hit.Source);
        Assert.Equal(1.0, hit.Coverage); // "found" counts as the passage's term, so full coverage still holds
    }

    [Fact]
    public void Plural_questions_match_singular_passages()
    {
        var index = new KeywordIndex();
        index.Upsert(Guid.NewGuid(), "Hosting cost: 40 per month on the Pro plan.", "pricing.csv");

        Assert.Equal("pricing.csv", Assert.Single(index.Search("hosting costs", 5)).Source);
    }
}
