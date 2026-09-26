import importlib.util
from pathlib import Path
from types import SimpleNamespace as State
import unittest
spec=importlib.util.spec_from_file_location('logic',Path(__file__).parent/'config/custom_components/base_layer/logic.py')
m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
def power(value,unit='W'):return State(state=str(value),attributes={'unit_of_measurement':unit})
class Tests(unittest.TestCase):
 def test_units_and_invalid(self):
  self.assertEqual(m.watts(power(11,'kW')),11000)
  for val in [power('unavailable'),power('nan'),power('inf'),power(-1),power(11,'kWh'),None]:self.assertIsNone(m.watts(val))
 def test_largest_running_eligible(self):
  devices=[{'switch':'a','power_sensor':'pa'},{'switch':'b','power_sensor':'pb'}]
  states={'a':State(state='on'),'b':State(state='on'),'pa':power(2500),'pb':power(5,'kW'),'unapproved':power(9000)}
  self.assertEqual(m.highest(states,devices)[1],'b')
  states['b'].state='off'
  self.assertEqual(m.highest(states,devices)[1],'a')
  states['pa']=power('unavailable')
  self.assertIsNone(m.highest(states,devices))
if __name__=='__main__':unittest.main()
