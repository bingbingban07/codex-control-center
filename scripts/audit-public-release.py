"""Audit source or portable directory. Reports locations/rule IDs, never matches."""
import argparse,json,pathlib,re,sys

def audit(root):
    root=pathlib.Path(root).resolve();issues=[];count=0
    excluded={'.git','bin','obj','__pycache__','work','verification','release','dist'}
    forbidden_names={'auth.json','preferences.json','connection.json','runtime.json','wallpaper.json','subagent-policy.json','minimax-media.json','image-profiles.json'}
    patterns=[('provider_secret',re.compile(rb'(?<![A-Za-z0-9_])(?:sk-(?:cp-)?[A-Za-z0-9_-]{35,}|user_[A-Za-z0-9]{45,}|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,})')),
        ('private_key',re.compile(rb'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'))]
    for path in root.rglob('*'):
        if not path.is_file() or set(path.relative_to(root).parts)&excluded:continue
        count+=1
        if path.name in forbidden_names or path.suffix.lower() in {'.dpapi','.token','.log','.jsonl','.bak','.lnk'}:issues.append({'file':str(path.relative_to(root)),'rule':'local_state_file'})
        data=path.read_bytes()
        for rule,pattern in patterns:
            if pattern.search(data):issues.append({'file':str(path.relative_to(root)),'rule':rule})
    return {'success':not issues,'filesScanned':count,'issues':issues}

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('root',nargs='?',default=str(pathlib.Path(__file__).resolve().parents[1]));a=p.parse_args();r=audit(a.root);print(json.dumps(r,ensure_ascii=False,indent=2));sys.exit(0 if r['success'] else 1)
