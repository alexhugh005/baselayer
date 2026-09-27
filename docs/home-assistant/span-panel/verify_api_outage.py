"""Exercise API circuit recovery in the localhost virtual lab, then reset to grid.

Requires the updated API running on the existing local demo database. This script
changes virtual circuit states; it never supports a remote Home Assistant target.
"""
import sys,time,json,sqlite3,datetime
from pathlib import Path
from lab_client import BASE,request,service
assert BASE in ('http://localhost:8123','http://127.0.0.1:8123'), 'Local virtual lab only'
ROOT=Path(__file__).resolve().parents[3]
DB=ROOT/'backend/src/BaseLayer.Api/data/base-layer-oauth.db'
OUT=ROOT/'artifacts/outage-recovery'
OUT.mkdir(parents=True,exist_ok=True)
def states(): return {x['entity_id']:x for x in request('/api/states')}
def priorities():
 return {k:x['state'] for k,x in states().items() if k.startswith('select.span_panel_') and k.endswith('_circuit_priority')}
def status():
 with sqlite3.connect('file:'+str(DB)+'?mode=ro',uri=True) as c:
  rows=c.execute("select OutageRecoveryJson from Homes where BaseUrl='http://localhost:8123' and Revoked=0").fetchall()
  assert len(rows)==1, 'Expected exactly one local demo home'
  return json.loads(rows[0][0])
def wait(check,description,seconds=100):
 end=time.monotonic()+seconds
 while time.monotonic()<end:
  value=check()
  if value:return value
  time.sleep(1)
 raise RuntimeError('Timeout: '+description)
def automation_idle(identifier):
 return all(x['attributes'].get('current',0)==0 for x in states().values() if x['attributes'].get('id')==identifier)
def reset():
 service('input_boolean','turn_off','input_boolean.lab_span_grid_outage')
 wait(lambda: states()['sensor.span_panel_grid_forming_entity']['state'].lower()=='grid','grid return')
 wait(lambda: automation_idle('lab_span_backup_end'),'grid recovery automation')
 service('script','lab_span_restore_supply')
 wait(lambda: sum(x['state']=='on' for k,x in states().items() if k.startswith('binary_sensor.lab_span_') and k.endswith('_supply'))==8,'all circuit supplies')
 service('script','base_layer_reset')
 wait(lambda: sum(x['WasOn'] for x in status().get('Baseline',[]))==8,'API pre-outage baseline')
 wait(lambda: {'light.virtual_living_room_light','switch.virtual_desk_lamp','switch.virtual_refrigerator'}.issubset({d['EntityId'] for c in status().get('Baseline',[]) for d in c.get('Devices',[])}),'pre-outage device baseline')
 print('Grid baseline: eight circuits and pre-outage devices captured.',flush=True)
original_priorities=priorities()
reset()
assert priorities()==original_priorities,'Reset changed circuit priorities'
service('fan','turn_on','fan.virtual_ceiling_fan')
wait(lambda: {'fan.virtual_ceiling_fan'}.issubset({d['EntityId'] for c in status().get('Baseline',[]) for d in c.get('Devices',[])}),'fan baseline')
expected_devices={d['EntityId']:d['TargetState'] for c in status()['Baseline'] if c['WasOn'] for d in c.get('Devices',[])}
service('input_boolean','turn_on','input_boolean.lab_span_grid_outage')
previous=None
evidence=[]
try:
 def complete():
  global previous
  s=status()
  assert priorities()==original_priorities,'Outage rollout changed circuit priorities'
  signature=(s['Status'],tuple((x['EntityId'],x['Status'],tuple((d['EntityId'],d['Status']) for d in x.get('Devices',[]))) for x in s.get('Circuits',[])))
  if signature!=previous:
   print(s['Status'],[(x['Name'],x['Status']) for x in s.get('Circuits',[])],flush=True)
   evidence.append(s)
   previous=signature
  if s['Status']=='blocked':raise RuntimeError('Rollout blocked')
  return s if s['Status']=='complete' and sum(x['Status']=='restored' for x in s['Circuits'])==8 else None
 result=wait(complete,'all eight circuits and their devices restored using settled measured load',300)
 with sqlite3.connect('file:'+str(DB)+'?mode=ro',uri=True) as c:
  commands=[dict(zip(['entityId','createdUtc','estimatedWatts','status'],r)) for r in c.execute('select EntityId,CreatedUtc,EstimatedWatts,Status from DeviceCommand where OutageEventId=? order by CreatedUtc',(result['EventId'],))]
 circuit_commands=[c for c in commands if c['entityId'].endswith('_breaker')]
 assert len(circuit_commands)==8,commands
 assert all(c['status']=='Confirmed' for c in commands),commands
 assert [c['estimatedWatts'] for c in circuit_commands]==sorted(c['estimatedWatts'] for c in circuit_commands)
 times=[datetime.datetime.fromisoformat(c['createdUtc']) for c in commands]
 gaps=[(b-a).total_seconds() for a,b in zip(times,times[1:])]
 assert all(g>=5 for g in gaps),gaps
 assert result['ReservedWatts']==result['MeasuredWatts']<=11000,result
 assert sum(c['estimatedWatts'] for c in circuit_commands)>11000, 'Test must demonstrate reuse of settled idle capacity'
 current=states()
 restored_devices={d['EntityId']:d['TargetState'] for c in result['Circuits'] if c['Status']=='restored' for d in c.get('Devices',[])}
 assert restored_devices, 'No pre-outage device states were captured'
 for entity,target in restored_devices.items():
  assert target==expected_devices[entity]
  assert current[entity]['state']==target,(entity,current[entity]['state'],target)
 assert current['switch.virtual_toaster']['state']=='off', 'Previously-off toaster was started'
 assert 0<float(current['sensor.virtual_household_power']['state'])<=11000
 assert sum(x['state']=='on' for k,x in current.items() if k.startswith('binary_sensor.lab_span_') and k.endswith('_supply'))==8
 (OUT/'live-verification.json').write_text(json.dumps({'result':result,'commands':commands,'gapsSeconds':gaps,'progress':evidence,'restoredDevices':restored_devices,'measuredWatts':float(current['sensor.virtual_household_power']['state'])},indent=2))
 print('PASS: lowest-first, eight confirmed circuits and their pre-outage devices, gaps >= 5 s, accounted load '+str(result['ReservedWatts'])+' W.',flush=True)
finally:
 reset()
 assert priorities()==original_priorities,'Grid return changed circuit priorities'
 print('Reset to normal grid power and quiet household.',flush=True)
