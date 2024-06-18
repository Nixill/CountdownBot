This folder contains the following gitignored files:

- `legalwords.txt`: A list of legal words, whether or not they're useful in any round. You can find them online, in sources such as [here](https://github.com/dwyl/english-words).
- `conundrums.txt`: A list of words that can be used in conundrums - 9 letters long with no direct anagrams.
- `words.txt`: A list of words that can be used in letters rounds - up to 9 letters long, up to 6 consonants (with Y always considered a consonant), up to 5 vowels.
- `exceptions.txt`: A list of exceptions to the list of legal words. This is a separate file so that `legalwords.txt` and its derivatives can be updated without losing the exceptions. It should be initialized to a blank file.
- `games.json`: Data of games in progress. This would be initiated to `[]`, but shouldn't be uploaded anywhere.

To initialize these files properly, you can run the `FILTER.ipynb` notebook with a .NET Interactive interpreter. Note that you must source the `legalwords.txt` file yourself, and exceptions will be built up over time.