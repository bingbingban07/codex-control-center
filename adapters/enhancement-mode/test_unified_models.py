import copy,importlib.util,json,pathlib,sys,unittest
sys.path.insert(0,str(pathlib.Path(__file__).parent))
from configure_unified_models import compose


class UnifiedModelsTests(unittest.TestCase):
    def test_same_named_models_have_distinct_aliases_and_routes(self):
        native={'models':[{'slug':'gpt-6.1-sol','display_name':'GPT Sol','visibility':'list','supported_reasoning_levels':[{'effort':'max'}]}]}
        catalog={'models':[{'slug':'gpt-6.1-sol','display_name':'GPT Sol [Key 1]'}, {'slug':'MiniMax-M3.1-Flash','display_name':'MiniMax'}]}
        router={'providers':{'a':{'key_env':'TEST_KEY'},'minimax':{'key_env':'TEST_MINIMAX'}},
            'routes':{'gpt-6.1-sol':{'provider':'a','model':'gpt-6.1-sol'},'MiniMax-M3.1-Flash':{'provider':'minimax','model':'MiniMax-M3.1-Flash'}}}
        originals=copy.deepcopy((native,catalog,router))
        combined,routes,renamed=compose(native,catalog,router,'C:/fixture')
        self.assertEqual(routes['routes']['gpt-6.1-sol']['provider'],'subscription')
        self.assertEqual(routes['routes']['key1/gpt-6.1-sol']['provider'],'a')
        self.assertEqual(routes['routes']['MiniMax-M3.1-Flash']['provider'],'minimax')
        self.assertEqual(renamed['gpt-6.1-sol'],'key1/gpt-6.1-sol')
        self.assertEqual((native,catalog,router),originals)
        again,again_routes,_=compose(native,combined,routes,'C:/fixture')
        self.assertEqual(again,combined);self.assertEqual(again_routes,routes)
        for model in combined['models']:self.assertEqual(routes['routes'][model['slug']]['provider'],model['provider'])
        self.assertEqual(combined['models'][0]['supported_reasoning_levels'],native['models'][0]['supported_reasoning_levels'])

    def test_no_official_catalog_refuses_partial_route_install(self):
        with self.assertRaises(ValueError):compose({'models':[]},{'models':[]},{'providers':{},'routes':{}},'C:/fixture')

    def test_custom_labels_survive_catalog_refresh(self):
        native={'models':[{'slug':'gpt-native','display_name':'GPT Native','context_window':1000}]}
        legacy={'models':[{'slug':'gpt-native','display_name':'GPT Native [我的订阅]'},
            {'slug':'key1/gpt-key','display_name':'GPT Key [备用]'},
            {'slug':'minimax-model','display_name':'MiniMax [工作]'}]}
        router={'providers':{},'routes':{'gpt-native':{'provider':'subscription','model':'gpt-native'},'key1/gpt-key':{'provider':'a','model':'gpt-key'},'minimax-model':{'provider':'minimax','model':'minimax-model'}}}
        refreshed,_,_=compose(native,legacy,router,'C:/fixture')
        self.assertEqual([m['display_name'] for m in refreshed['models']],[m['display_name'] for m in legacy['models']])
        self.assertEqual(refreshed['models'][0]['context_window'],1000)


if __name__=='__main__':unittest.main()
