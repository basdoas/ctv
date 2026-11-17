
using Xunit;
using Ctv;

namespace ctv.Tests;

public class QuestionToolsTests
{
    [Fact]
    public void ParseQuestions_ExtractsChoiceQuestion()
    {
        var text = @"<ask_question id=""q1"" type=""choice""><question>Pick one?</question><option label=""A"" desc=""First""/></ask_question>";
        var questions = QuestionTools.ParseQuestions(text);
        Assert.Single(questions);
        Assert.Equal("q1", questions[0].Id);
        Assert.Equal("Pick one?", questions[0].Question);
    }

    [Fact]
    public void ParseQuestions_NoQuestions_ReturnsEmpty()
    {
        var questions = QuestionTools.ParseQuestions("No questions here.");
        Assert.Empty(questions);
    }
}
