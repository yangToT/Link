import copy
import unittest
from layer2_transport import configure_transport


class TransportTest(unittest.TestCase):
    def test_preserves_options_and_is_idempotent(self):
        state = {'HubName_str': 'TEST', 'AdminOptionList': [
            {'Name_str': 'DisableUdpAcceleration', 'Value_u32': 0},
            {'Name_str': 'OtherOption', 'Value_u32': 37}]}
        writes = []
        def rpc(method, params):
            if method == 'SetHubExtOptions':
                writes.append(copy.deepcopy(params))
                state.update(copy.deepcopy(params))
            return copy.deepcopy(state)
        configure_transport(rpc, 'TEST')
        configure_transport(rpc, 'TEST')
        self.assertEqual(len(writes), 1)
        self.assertEqual(state['AdminOptionList'][1]['Value_u32'], 37)
        self.assertEqual(state['AdminOptionList'][0]['Value_u32'], 1)

    def test_unknown_version_does_not_write(self):
        calls = []
        def rpc(method, params):
            calls.append(method)
            return {'AdminOptionList': []}
        with self.assertRaises(RuntimeError):
            configure_transport(rpc, 'TEST')
        self.assertEqual(calls, ['GetHubExtOptions'])

    def test_failed_readback_restores_original_options(self):
        original = {'HubName_str': 'TEST', 'AdminOptionList': [
            {'Name_str': 'DisableUdpAcceleration', 'Value_u32': 0}]}
        writes = []
        def rpc(method, params):
            if method == 'SetHubExtOptions':
                writes.append(copy.deepcopy(params))
            return copy.deepcopy(original)
        with self.assertRaises(RuntimeError):
            configure_transport(rpc, 'TEST')
        self.assertEqual(len(writes), 2)
        self.assertEqual(writes[-1], original)


if __name__ == '__main__':
    unittest.main()
