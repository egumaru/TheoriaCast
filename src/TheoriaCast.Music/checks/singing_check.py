from pathlib import Path
import sys
import unittest
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/"worker"))
from singing import prepare,audit


class SingingTests(unittest.TestCase):
    def test_entire_sheet_without_staging(self):
        spec={"theme":"金融","lyrics":"[Intro]\n短いピアノの導入\n[Verse]市場と根菜\n二〇一九年の市場（間奏）"}
        sheet,rules,_=prepare(spec,{})
        self.assertEqual(sheet,"しじょうとこんさい\nにせんじゅうきゅうねんのしじょう")
        self.assertEqual(rules["市場"],"しじょう")
        self.assertIn("[Intro]",spec["lyrics"])

    def test_ambiguous_or_missing_words(self):
        self.assertEqual(prepare({"theme":"","lyrics":"一二三四 気に入る"}, {})[0],"一二三四 きにいる")
        self.assertTrue(audit("しじょう","いちば",None))
        self.assertTrue(audit("あさ あさ","あさ",None))
        self.assertFalse(audit("あさ あさ","アサ、あさ",None))

    def test_line_boundaries_do_not_repeat_the_previous_lyric(self):
        class IsolatedLines:
            def convert(self,line):
                assert "\n" not in line
                return [{"hira":line}]
        self.assertEqual(audit("あさのひかり\nまどをあけよう","あさのひかり",IsolatedLines()),
                         ["予定「まどをあけよう」／聞き取り「なし」"])

if __name__=="__main__":
    unittest.main()
