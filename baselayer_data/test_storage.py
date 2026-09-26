import os
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest.mock import patch

import pandas as pd

from baselayer_data import data_root, dataset_root, list_datasets, load, logs_root, save
from baselayer_data.logs import get_logger


class DataRootTests(unittest.TestCase):
    def test_defaults_to_current_directory(self):
        with TemporaryDirectory() as tmp, patch.dict(os.environ, {}, clear=False):
            os.environ.pop("DATA_ROOT", None)
            cwd = os.getcwd()
            os.chdir(tmp)
            try:
                self.assertEqual(data_root(), Path(tmp).resolve())
                self.assertEqual(dataset_root(), Path(tmp).resolve() / "dataset")
            finally:
                os.chdir(cwd)

    def test_reads_data_root_on_each_call(self):
        with TemporaryDirectory() as tmp, patch.dict(os.environ, {"DATA_ROOT": tmp}):
            self.assertEqual(dataset_root(), Path(tmp).resolve() / "dataset")
            self.assertEqual(logs_root(), Path(tmp).resolve() / "logs")

    def test_save_load_and_list_under_data_root(self):
        with TemporaryDirectory() as tmp, patch.dict(os.environ, {"DATA_ROOT": tmp}):
            frame = pd.DataFrame({"a": [1, 2]})
            path = save(frame, "demo/table")
            save(frame, "demo/raw/skipped")
            self.assertEqual(path, Path(tmp).resolve() / "dataset" / "demo" / "table.parq")
            self.assertTrue(load("demo/table").equals(frame))
            self.assertEqual(list_datasets(), ["demo/table"])

    def test_logger_writes_under_logs_root(self):
        with TemporaryDirectory() as tmp, patch.dict(os.environ, {"DATA_ROOT": tmp}):
            logger = get_logger("unit-test")
            with patch("sys.stdout"):
                logger.info("hello")
            for handler in logger.handlers:
                handler.flush()
            text = (Path(tmp) / "logs" / "unit-test.log").read_text()
            self.assertIn("hello", text)
            for handler in list(logger.handlers):
                logger.removeHandler(handler)
                handler.close()


if __name__ == "__main__":
    unittest.main()
