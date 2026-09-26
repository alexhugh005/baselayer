import importlib.util
from pathlib import Path
import unittest
spec = importlib.util.spec_from_file_location('meter', Path(__file__).parent/'config/custom_components/energy_guard/meter.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

class AccountingTests(unittest.TestCase):
    def test_boundary_and_new_session(self):
        m = module.SessionMeter()
        self.assertFalse(m.update(True, 50))
        self.assertFalse(m.update(True, 51))
        self.assertTrue(m.update(True, 51.01))
        m.update(False, 51.01)
        self.assertFalse(m.update(True, 51.01))
        self.assertEqual(m.used, 0)
    def test_restart_and_unavailable(self):
        m = module.SessionMeter()
        m.update(True, 10)
        m.update(True, 10.6)
        m = module.SessionMeter(m.dump())
        for value in [None, float('nan'), float('inf'), -1]:
            self.assertFalse(m.update(True, value))
        self.assertTrue(m.update(True, 11.1))
    def test_meter_reset(self):
        m = module.SessionMeter()
        m.update(True, 10)
        m.update(True, 10.6)
        self.assertFalse(m.update(True, 0.1))
        self.assertTrue(m.update(True, 0.6))

if __name__ == '__main__': unittest.main()
