import importlib.util,json,unittest,tempfile,subprocess
from pathlib import Path
spec=importlib.util.spec_from_file_location('collector',Path(__file__).with_name('collect_repository.py'));collector=importlib.util.module_from_spec(spec);spec.loader.exec_module(collector)
class CollectorTests(unittest.TestCase):
 def test_only_tracked_declarations_and_no_credentials(self):
  with tempfile.TemporaryDirectory() as name:
   root=Path(name);subprocess.run(['git','init','-q',name],check=True)
   def put(path,content):
    f=root/path;f.parent.mkdir(parents=True,exist_ok=True);f.write_text(content)
   put('ZibasheERP.API/Controllers/Test.cs','[Route("api/Test")] [Authorize(Roles = "Admin")] class T{[HttpGet]void Get(){}}')
   put('ZibasheERP.API/Program.cs','AddHostedService<Worker>()')
   put('docs/web-continuity/capability-register.csv','شناسه,حوزه\nID-01,هویت\n')
   subprocess.run(['git','add','.'],cwd=root,check=True)
   subprocess.run(['git','-c','user.name=Test','-c','user.email=test@example.invalid','commit','-qm','fixture'],cwd=root,check=True)
   put('.env.production','TOKEN=never-export-this')
   put('ZibasheERP.API/Controllers/Untracked.cs','[Route("private")]')
   result=collector.collect(root);encoded=json.dumps(result)
   self.assertNotIn('never-export-this',encoded);self.assertNotIn('Untracked',encoded)
   self.assertEqual(result['controllers'][0]['roles'],['Admin']);self.assertEqual(result['capabilities'],1)
   self.assertFalse(result['runtime_verified']);self.assertFalse(result['financial_writes'])
if __name__=='__main__':unittest.main()
