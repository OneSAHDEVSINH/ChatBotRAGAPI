namespace ChatBotRAGAPI.Services;

/// <summary>
/// The Porter (1980) stemmer for English, used by <see cref="KeywordIndex"/> so a question's "founded" meets a
/// passage's "found" and "costs" meets "cost". Words with digits and words of one or two letters are left as
/// they are (codes such as "q3" or "x86" must match exactly). Input is expected lower-case.
/// Reference: M.F. Porter, "An algorithm for suffix stripping", Program 14(3), 1980.
/// </summary>
public static class PorterStemmer
{
    public static string Stem(string word)
    {
        if (word.Length <= 2 || !word.All(c => c is >= 'a' and <= 'z')) return word;

        var w = new Word(word);
        w.Step1a(); w.Step1b(); w.Step1c(); w.Step2(); w.Step3(); w.Step4(); w.Step5a(); w.Step5b();
        return w.ToString();
    }

    private sealed class Word(string s)
    {
        private char[] _b = s.ToCharArray();
        private int _k = s.Length - 1; // last index of the current word
        private int _j;                // end of the stem before a matched suffix

        public override string ToString() => new(_b, 0, _k + 1);

        private bool IsConsonant(int i) => _b[i] switch
        {
            'a' or 'e' or 'i' or 'o' or 'u' => false,
            'y' => i == 0 || !IsConsonant(i - 1),
            _ => true,
        };

        /// <summary>m: the number of vowel-consonant sequences in b[0..j].</summary>
        private int Measure()
        {
            var n = 0; var i = 0;
            while (true) { if (i > _j) return n; if (!IsConsonant(i)) break; i++; }
            i++;
            while (true)
            {
                while (true) { if (i > _j) return n; if (IsConsonant(i)) break; i++; }
                i++; n++;
                while (true) { if (i > _j) return n; if (!IsConsonant(i)) break; i++; }
                i++;
            }
        }

        private bool VowelInStem()
        {
            for (var i = 0; i <= _j; i++) if (!IsConsonant(i)) return true;
            return false;
        }

        private bool DoubleConsonant(int i) => i >= 1 && _b[i] == _b[i - 1] && IsConsonant(i);

        /// <summary>cvc at i, where the second c is not w, x or y (hop, not hoop or snow).</summary>
        private bool Cvc(int i)
        {
            if (i < 2 || !IsConsonant(i) || IsConsonant(i - 1) || !IsConsonant(i - 2)) return false;
            return _b[i] is not ('w' or 'x' or 'y');
        }

        private bool Ends(string suffix)
        {
            var len = suffix.Length;
            if (len > _k + 1) return false;
            for (var i = 0; i < len; i++) if (_b[_k - len + 1 + i] != suffix[i]) return false;
            _j = _k - len;
            return true;
        }

        private void SetTo(string s)
        {
            var len = s.Length;
            if (_j + 1 + len > _b.Length) Array.Resize(ref _b, _j + 1 + len);
            for (var i = 0; i < len; i++) _b[_j + 1 + i] = s[i];
            _k = _j + len;
        }

        private void ReplaceIfMeasured(string s) { if (Measure() > 0) SetTo(s); }

        /// <summary>Plurals: caresses→caress, ponies→poni, cats→cat.</summary>
        public void Step1a()
        {
            if (_b[_k] != 's') return;
            if (Ends("sses")) _k -= 2;
            else if (Ends("ies")) SetTo("i");
            else if (_k >= 1 && _b[_k - 1] != 's') _k--;
        }

        /// <summary>-ed, -ing: agreed→agree, plastered→plaster, motoring→motor, hopping→hop, filing→file.</summary>
        public void Step1b()
        {
            if (Ends("eed")) { if (Measure() > 0) _k--; return; }
            if (!((Ends("ed") || Ends("ing")) && VowelInStem())) return;

            _k = _j;
            if (Ends("at")) SetTo("ate");
            else if (Ends("bl")) SetTo("ble");
            else if (Ends("iz")) SetTo("ize");
            else if (DoubleConsonant(_k)) { if (_b[_k] is not ('l' or 's' or 'z')) _k--; }
            else { _j = _k; if (Measure() == 1 && Cvc(_k)) SetTo("e"); }
        }

        /// <summary>Terminal y → i when the stem has a vowel: happy→happi.</summary>
        public void Step1c()
        {
            if (Ends("y") && VowelInStem()) _b[_k] = 'i';
        }

        public void Step2()
        {
            if (_k < 1) return;
            switch (_b[_k - 1])
            {
                case 'a': if (Ends("ational")) ReplaceIfMeasured("ate"); else if (Ends("tional")) ReplaceIfMeasured("tion"); break;
                case 'c': if (Ends("enci")) ReplaceIfMeasured("ence"); else if (Ends("anci")) ReplaceIfMeasured("ance"); break;
                case 'e': if (Ends("izer")) ReplaceIfMeasured("ize"); break;
                case 'l':
                    if (Ends("bli")) ReplaceIfMeasured("ble"); else if (Ends("alli")) ReplaceIfMeasured("al");
                    else if (Ends("entli")) ReplaceIfMeasured("ent"); else if (Ends("eli")) ReplaceIfMeasured("e");
                    else if (Ends("ousli")) ReplaceIfMeasured("ous");
                    break;
                case 'o': if (Ends("ization")) ReplaceIfMeasured("ize"); else if (Ends("ation")) ReplaceIfMeasured("ate"); else if (Ends("ator")) ReplaceIfMeasured("ate"); break;
                case 's':
                    if (Ends("alism")) ReplaceIfMeasured("al"); else if (Ends("iveness")) ReplaceIfMeasured("ive");
                    else if (Ends("fulness")) ReplaceIfMeasured("ful"); else if (Ends("ousness")) ReplaceIfMeasured("ous");
                    break;
                case 't': if (Ends("aliti")) ReplaceIfMeasured("al"); else if (Ends("iviti")) ReplaceIfMeasured("ive"); else if (Ends("biliti")) ReplaceIfMeasured("ble"); break;
                case 'g': if (Ends("logi")) ReplaceIfMeasured("log"); break;
            }
        }

        public void Step3()
        {
            switch (_b[_k])
            {
                case 'e':
                    if (Ends("icate")) ReplaceIfMeasured("ic"); else if (Ends("ative")) ReplaceIfMeasured("");
                    else if (Ends("alize")) ReplaceIfMeasured("al");
                    break;
                case 'i': if (Ends("iciti")) ReplaceIfMeasured("ic"); break;
                case 'l': if (Ends("ical")) ReplaceIfMeasured("ic"); else if (Ends("ful")) ReplaceIfMeasured(""); break;
                case 's': if (Ends("ness")) ReplaceIfMeasured(""); break;
            }
        }

        public void Step4()
        {
            if (_k < 1) return;
            var matched = _b[_k - 1] switch
            {
                'a' => Ends("al"),
                'c' => Ends("ance") || Ends("ence"),
                'e' => Ends("er"),
                'i' => Ends("ic"),
                'l' => Ends("able") || Ends("ible"),
                'n' => Ends("ant") || Ends("ement") || Ends("ment") || Ends("ent"),
                'o' => (Ends("ion") && _j >= 0 && _b[_j] is 's' or 't') || Ends("ou"),
                's' => Ends("ism"),
                't' => Ends("ate") || Ends("iti"),
                'u' => Ends("ous"),
                'v' => Ends("ive"),
                'z' => Ends("ize"),
                _ => false,
            };
            if (matched && Measure() > 1) _k = _j;
        }

        /// <summary>Final e: probate→probat, rate stays.</summary>
        public void Step5a()
        {
            _j = _k;
            if (_b[_k] != 'e') return;
            _j = _k - 1;
            var m = Measure();
            if (m > 1 || (m == 1 && !Cvc(_k - 1))) _k--;
        }

        /// <summary>-ll → -l when m > 1: controll→control.</summary>
        public void Step5b()
        {
            _j = _k;
            if (_b[_k] == 'l' && DoubleConsonant(_k) && Measure() > 1) _k--;
        }
    }
}
