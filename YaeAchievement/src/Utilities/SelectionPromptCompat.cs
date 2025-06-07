using Spectre.Console;

namespace YaeAchievement.Utilities;

public sealed class SelectionPromptCompat<T> where T : notnull {

    private readonly List<T> _choices = [];
    private readonly SelectionPrompt<T> _prompt = new ();

    public SelectionPromptCompat<T> Title(string? title) {
        _prompt.Title = title;
        return this;
    }

    public SelectionPromptCompat<T> AddChoices(params IEnumerable<T> choices) {
        foreach (var choice in choices) {
            _prompt.AddChoice(choice);
            _choices.Add(choice);
        }
        return this;
    }

    public T Prompt() {
        if (AnsiConsole.Profile.Capabilities.Ansi) {
            var title = _prompt.Title;
            _prompt.Title += $" ({App.SelectionPromptCompatAnsiTip})";
            var result = AnsiConsole.Prompt(_prompt);
            _prompt.Title = title;
            return result;
        }
        if (_prompt.Title != null) {
            AnsiConsole.WriteLine(_prompt.Title + $" ({App.SelectionPromptCompatNonAnsiTip})");
        }
        for (var i = 0; i < _choices.Count; i++) {
            var choice = _choices[i];
            AnsiConsole.WriteLine($"[{i}] {choice}");
        }
        var choosePrompt = new TextPrompt<int>(App.SelectionPromptCompatChooseOne).Validate(i => {
            if (i < 0 || i >= _choices.Count) {
                return ValidationResult.Error(string.Format(App.SelectionPromptCompatInvalidChoice, _choices.Count - 1));
            }
            return ValidationResult.Success();
        });
        var resultIndex = AnsiConsole.Prompt(choosePrompt);
        return _choices[resultIndex];
    }
}
