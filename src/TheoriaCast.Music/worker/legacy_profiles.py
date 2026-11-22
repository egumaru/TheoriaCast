"""Read the two historical profile formats written before engine manifests existed."""
import re

# Only these two profiles were produced by the old installer. Revisions and
# license text are read from the installed profile, including local overrides.
_REPOSITORIES={
    "ACE-Step/ACE-Step-v1-3.5B":("https://github.com/ace-step/ACE-Step","LICENSE"),
    "m-a-p/YuE2-3B":("https://github.com/multimodal-art-projection/YuE","MODEL_LICENSE"),
}

def metadata(profile):
    model=profile["model"]
    if model not in _REPOSITORIES:
        return None
    versions=dict(re.findall(r"([\w-]+)@([0-9a-f]{40})",profile["version"]))
    code=versions.pop("code",None)
    if not code or not versions:
        return None
    repo,license_file=_REPOSITORIES[model]
    components=[]
    for name,revision in versions.items():
        source=model if name=="weights" else model.split("/")[0]+"/"+name
        components.append({"name":source,"role":"decoder" if name.endswith("Vae") else "generator",
                           "source":"https://huggingface.co/"+source,"revision":revision,"license":profile["license"],
                           "license_evidence":repo+"/blob/"+code+"/"+license_file})
    return {"code_repository":repo,"code_revision":code,"local_patch_sha256":None},components
