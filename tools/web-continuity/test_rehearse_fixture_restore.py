import importlib.util,unittest
from pathlib import Path
spec=importlib.util.spec_from_file_location('restore',Path(__file__).with_name('rehearse_fixture_restore.py'));r=importlib.util.module_from_spec(spec);spec.loader.exec_module(r)
class RestoreGuardTests(unittest.TestCase):
 def fixture(self):return {'Name':'/zibashe-onboarding-sql','Config':{'Labels':{'zibashe.onboarding':'development'}},'HostConfig':{'PortBindings':{'1433/tcp':[{'HostIp':'127.0.0.1','HostPort':'14333'}]}}}
 def test_accepts_only_development_label(self):
  x=self.fixture();r.validate(x);x['Config']['Labels']={}
  with self.assertRaises(ValueError):r.validate(x)
 def test_refuses_production_name(self):
  x=self.fixture();x['Name']='/deploy-sqlserver-1'
  with self.assertRaises(ValueError):r.validate(x)
 def test_refuses_exposed_or_different_port(self):
  for ip,port in [('0.0.0.0','14333'),('127.0.0.1','1433')]:
   x=self.fixture();x['HostConfig']['PortBindings']['1433/tcp']=[{'HostIp':ip,'HostPort':port}]
   with self.assertRaises(ValueError):r.validate(x)
if __name__=='__main__':unittest.main()
